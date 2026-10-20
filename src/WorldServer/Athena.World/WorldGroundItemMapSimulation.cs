using Athena.Net.World.Contracts;

namespace Athena.Net.World;

// Composition-time override for WorldGroundItemMapSimulation's own lifetime/deadline/dedup-window
// defaults - same rationale as WorldMonsterTouchedWindowOptions: lets a test inject much shorter
// durations and observe the real expiry/unresolved-claim-reap/dedup behavior deterministically
// within a bounded real-wall-clock wait, without the production defaults ever needing to change.
public sealed record WorldGroundItemOptions(TimeSpan? Lifetime = null, TimeSpan? UnresolvedClaimDeadline = null, TimeSpan? DropDedupWindow = null);

// Feature 16 S2/S3: one map's worth of World-authoritative ground-item state, owned entirely by
// WorldPartitionGrain (never its own grain - same "one coarse WorldPartitionGrain, no
// MonsterGrain/MapGrain/CellGrain/ItemGrain" architecture as WorldMonsterMapSimulation/
// WorldPlayerMapSimulation). Ordinary ground items are cheap, ephemeral world state - unlike
// monster simulation, never unloaded by a touched-window policy (nothing here is expensive to keep
// resident, and a dropped item's lifetime must survive regardless of how "active" the map looks to
// that unrelated policy) - but a fresh grain activation (process restart) DOES mint a fresh Epoch,
// exactly like WorldPlayerMapSimulation's own Epoch, so a pre-restart GroundItemId can never
// collide with an unrelated post-restart item reusing the same small id.
//
// Ownership-window simplification (documented, not a silent gap): pinned pc_takeitem (pc.cpp:6188-
// 6253) models a three-tier first/second/third-top-attacker loot priority chain plus party-share.
// This project has no multi-attacker damage-attribution history and no party system yet (only the
// single killing-blow AttackerCharacterId is ever known - see WorldMonsterDamageCommand) - so
// WorldGroundItemOwnership models ONLY the first tier (a single owning character, exclusive for
// OwnerWindowMs, matching pinned item_first_get_time's role), never the second/third tiers or
// party-share. Upgrade this once multi-attacker attribution and a party system exist.
//
// Claim is a SEPARATE, EXCLUSIVE reservation step before any durable grant is attempted - this is
// the entire mechanism that makes "two players claim the same ground item at the same instant"
// resolve to exactly one winner: both RPCs arrive at this SAME grain activation, which Orleans
// already serializes into one-at-a-time turns, so whichever ClaimGroundItemAsync call is processed
// first simply wins the in-memory compare-and-set below - no distributed lock, no two-phase commit,
// no new infrastructure. The caller only proceeds to ask CharServer for a durable grant AFTER
// winning this claim, and must call CompleteGroundItemClaimAsync (permanently remove) or
// ReleaseGroundItemClaimAsync (return to Available, e.g. the grant failed - full inventory) exactly
// once afterward - seeWorldPartitionGrain's own RPC doc comments for the full crash/retry contract
// this two-step split produces.
internal sealed class WorldGroundItemMapSimulation
{
    private sealed class Entry
    {
        public required uint GroundItemId { get; init; }
        public required ushort X { get; init; }
        public required ushort Y { get; init; }
        public required WorldGroundItemContent Content { get; init; }
        public required WorldGroundItemOwnership Ownership { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        // null = Available (unclaimed). Set the instant a claim wins; cleared only by Release (never
        // by expiry - see ClaimDeadlineUtc's own doc comment for why an UNRESOLVED claim is reaped by
        // permanent removal, never by quietly reverting to Available).
        public (Guid ClaimId, uint CharacterId)? Claim { get; set; }
        // Set the instant a claim wins, alongside Claim itself: the latest moment an UNRESOLVED claim
        // (the caller never called Complete or Release - e.g. the claiming MapServer crashed between
        // winning the claim and learning its CharServer grant's outcome) is still honored as
        // exclusively reserved. Past this deadline the item is permanently removed (see SweepExpired)
        // - NEVER silently returned to Available, because World has no way to know whether the
        // original claimant's CharServer grant already committed; handing the same item to a SECOND
        // character here could duplicate an already-granted item. This matches the project's existing
        // "ground items are ephemeral world state, not a durable entity" decision: a WorldServer
        // restart already loses all ground-item state unconditionally (fresh Epoch, empty state) -
        // this is the same acceptable-loss policy applied to one orphaned claim instead of a whole
        // restart, never a recovery/lease/renewal mechanism.
        public DateTimeOffset? ClaimDeadlineUtc { get; set; }
    }

    private readonly Dictionary<uint, Entry> _byId = [];
    // DropId -> (GroundItemId, DeadlineUtc): lets a retried DropGroundItemAsync call (the SAME
    // logical drop, e.g. a monster-death event retried after a transient RPC failure) answer
    // AlreadyDropped instead of minting a second physical item - see WorldGroundItemDrop.DropId's
    // own doc comment. DeadlineUtc is NEVER set independently of the item's own lifetime: while the
    // item is still alive in _byId, this record's deadline is kept pinned to (at least) the item's
    // own ExpiresAtUtc/ClaimDeadlineUtc, so a dedup record can never expire out from under a still-
    // live item (the bug a 30s-vs-60s mismatch previously allowed: a retry after the dedup window
    // but before the item's own longer lifetime elapsed would wrongly mint a duplicate). Only once
    // the item actually leaves the world (Remove) does the deadline become a plain, bounded
    // DropDedupWindow-from-now value - specifically so a DELAYED retry that arrives after the
    // original item was already picked up/expired still finds the dedup record and answers
    // AlreadyDropped instead of creating a duplicate. A drop event older than that post-removal
    // window is assumed to have no more in-flight retries and is reaped (SweepExpired), so a
    // genuinely NEW drop that happens to reuse the same caller-chosen Guid (extremely unlikely,
    // caller's own responsibility) is never blocked forever.
    private readonly Dictionary<Guid, (uint GroundItemId, DateTimeOffset DeadlineUtc)> _dropDedup = [];

    public string MapId { get; }
    public WorldSimulationEpoch Epoch { get; }
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan _unresolvedClaimDeadline;
    private readonly TimeSpan _dropDedupWindow;

    public WorldGroundItemMapSimulation(string mapId, TimeProvider timeProvider, TimeSpan? lifetime = null, TimeSpan? unresolvedClaimDeadline = null, TimeSpan? dropDedupWindow = null)
    {
        MapId = mapId;
        Epoch = WorldSimulationEpoch.NewEpoch();
        _timeProvider = timeProvider;
        // Pinned flooritem_lifetime battle-conf default (60000ms) - see the plan doc's own "Pinned
        // battle configuration" section. A conf-driven override is future work; no conf plumbing
        // exists for World yet.
        _lifetime = lifetime ?? TimeSpan.FromMilliseconds(60_000);
        // No pinned reference value exists for this (pinned rAthena is single-process and never faces
        // this failure mode) - chosen as a short bound on the window a crashed caller can block one
        // item, never a lease/renewal deadline. A caller's own CharServer round trip is expected to
        // resolve in low milliseconds to a few seconds even under retry; this leaves ample margin
        // while still bounding the loss to a predictable, short window.
        _unresolvedClaimDeadline = unresolvedClaimDeadline ?? TimeSpan.FromSeconds(30);
        _dropDedupWindow = dropDedupWindow ?? TimeSpan.FromSeconds(30);
    }

    // Expiry is lazy/on-access (exactly like WorldPresenceLease's own "World decides expiry on its
    // own clock, on every call that touches the relevant state" convention) - no dedicated timer.
    private void SweepExpired()
    {
        var now = _timeProvider.GetUtcNow();
        if (_byId.Count > 0)
        {
            List<uint>? toRemove = null;
            foreach (var (id, entry) in _byId)
            {
                var expired = entry.Claim is null
                    ? entry.ExpiresAtUtc <= now // Available: ordinary flooritem_lifetime expiry.
                    : entry.ClaimDeadlineUtc <= now; // Claimed-but-unresolved past its deadline: permanently removed, never released - see ClaimDeadlineUtc's own doc comment.
                if (expired) (toRemove ??= []).Add(id);
            }
            if (toRemove is not null) foreach (var id in toRemove) Remove(id);
        }

        if (_dropDedup.Count == 0) return;
        List<Guid>? staleDedup = null;
        foreach (var (dropId, record) in _dropDedup)
        {
            // Never reap a dedup record whose item is STILL ALIVE, regardless of DeadlineUtc - the
            // deadline only governs the bounded post-removal retry window (see this field's own doc
            // comment). A live item's own SweepExpired branch above (Available ExpiresAtUtc or
            // Claimed ClaimDeadlineUtc) is the sole authority for when it actually leaves the world;
            // only THAT event (via Remove) ever starts this record's real countdown.
            if (_byId.ContainsKey(record.GroundItemId)) continue;
            if (record.DeadlineUtc <= now) (staleDedup ??= []).Add(dropId);
        }
        if (staleDedup is not null) foreach (var dropId in staleDedup) _dropDedup.Remove(dropId);
    }

    // Called on every path an item permanently leaves the world (ordinary expiry, claim completion,
    // or an unresolved-claim deadline reap) - in every case, the dedup record's deadline is refreshed
    // from THIS moment, so a drop-event retry that was merely delayed (not lost) still finds it and
    // answers AlreadyDropped for the full DropDedupWindow AFTER the item actually left the world,
    // not just after it was created.
    private void Remove(uint groundItemId)
    {
        if (!_byId.Remove(groundItemId)) return;
        foreach (var (dropId, record) in _dropDedup)
        {
            if (record.GroundItemId != groundItemId) continue;
            _dropDedup[dropId] = (groundItemId, _timeProvider.GetUtcNow() + _dropDedupWindow);
            break;
        }
    }

    // Non-mutating dedup check - called BEFORE the caller allocates a GroundItemId (from the shared
    // global actor-ID namespace - see WorldPartitionGrain.GroundItemActorIdAllocator's own doc
    // comment) so a dedup hit never burns an id for nothing. SweepExpired still runs here (not only
    // in Drop) so a caller who dedup-hits on their very first check still observes fresh state.
    public bool TryGetExistingDrop(Guid dropId, out uint groundItemId)
    {
        SweepExpired();
        if (_dropDedup.TryGetValue(dropId, out var existing)) { groundItemId = existing.GroundItemId; return true; }
        groundItemId = default;
        return false;
    }

    // `groundItemId` is caller-allocated (from the shared global actor-ID namespace) - this method
    // never mints its own id. The caller must have already confirmed TryGetExistingDrop returned
    // false for this EXACT drop.DropId in the same logical attempt (WorldPartitionGrain.
    // DropGroundItemAsync does this immediately before allocating, under the same grain turn, so no
    // other call can interleave between the check and this commit).
    public WorldGroundItemDropResult Drop(WorldGroundItemDrop drop, uint groundItemId)
    {
        if (_dropDedup.TryGetValue(drop.DropId, out var existing)) return new(WorldGroundItemDropStatus.AlreadyDropped, existing.GroundItemId);

        var id = groundItemId;
        var now = _timeProvider.GetUtcNow();
        _byId[id] = new Entry
        {
            GroundItemId = id,
            X = drop.X,
            Y = drop.Y,
            Content = drop.Content,
            Ownership = drop.Ownership,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + _lifetime,
        };
        // This initial deadline is a placeholder, never actually consulted while the item is alive -
        // SweepExpired's dedup sweep skips any record whose GroundItemId still exists in _byId,
        // regardless of DeadlineUtc (see that method's own doc comment). Remove overwrites this with
        // the REAL post-removal deadline the instant the item actually leaves the world.
        _dropDedup[drop.DropId] = (id, now + _dropDedupWindow);
        return new(WorldGroundItemDropStatus.Dropped, id);
    }

    public WorldGroundItemClaimResult Claim(WorldGroundItemClaim claim)
    {
        SweepExpired();
        if (!_byId.TryGetValue(claim.Item.GroundItemId, out var entry))
            return new(WorldGroundItemClaimStatus.NotFound, null);

        if (entry.Claim is { } existing)
        {
            // Issue 3: a repeated ClaimId must ALSO match its original CharacterId - a caller-minted
            // Guid colliding is not proof of "the same character retrying," and returning Content to
            // whichever character merely GUESSES or reuses a leaked ClaimId would leak the pickup to
            // the wrong character.
            return existing.ClaimId == claim.ClaimId && existing.CharacterId == claim.CharacterId
                ? new(WorldGroundItemClaimStatus.AlreadyClaimed, entry.Content)
                : new(WorldGroundItemClaimStatus.AlreadyClaimedByOther, null);
        }

        // pc_takeitem's item_get_tick gate (pc.cpp:6209-6227), first tier only - see this type's own
        // doc comment for the documented second/third-tier/party-share scope simplification.
        if (entry.Ownership.OwnerCharacterId is { } owner && owner != claim.CharacterId)
        {
            var windowEnds = entry.CreatedAtUtc + TimeSpan.FromMilliseconds(entry.Ownership.OwnerWindowMs);
            if (_timeProvider.GetUtcNow() < windowEnds) return new(WorldGroundItemClaimStatus.NotEligible, null);
        }

        entry.Claim = (claim.ClaimId, claim.CharacterId);
        entry.ClaimDeadlineUtc = _timeProvider.GetUtcNow() + _unresolvedClaimDeadline;
        return new(WorldGroundItemClaimStatus.Claimed, entry.Content);
    }

    private WorldGroundItemCompletionResult EndClaim(WorldGroundItemClaimReference reference, bool complete)
    {
        if (!_byId.TryGetValue(reference.Item.GroundItemId, out var entry))
            // Not found could mean "never existed" OR "already completed/reaped" - both answer the
            // same safe AlreadyApplied/NotFound split the caller cannot usefully distinguish further
            // without a separate permanent tombstone ledger; completion (and the unresolved-claim
            // deadline reap) already permanently removes the entry, so a replayed Complete after a
            // successful one correctly finds nothing here.
            return new(complete ? WorldGroundItemCompletionStatus.AlreadyApplied : WorldGroundItemCompletionStatus.NotFound);

        if (entry.Claim is not { } held || held.ClaimId != reference.ClaimId)
            return new(WorldGroundItemCompletionStatus.NotFound);

        if (complete)
        {
            Remove(reference.Item.GroundItemId);
        }
        else
        {
            // Release: the caller has CONFIRMED (via CharServer's own idempotent operation log - see
            // CharacterInventoryService's own doc comment) that no durable mutation occurred for this
            // attempt - only then is it safe to return the item to Available for anyone to claim.
            entry.Claim = null;
            entry.ClaimDeadlineUtc = null;
        }
        return new(WorldGroundItemCompletionStatus.Applied);
    }

    public WorldGroundItemCompletionResult Complete(WorldGroundItemClaimReference reference) => EndClaim(reference, complete: true);
    public WorldGroundItemCompletionResult Release(WorldGroundItemClaimReference reference) => EndClaim(reference, complete: false);

    public WorldGroundItemSnapshot BuildSnapshot()
    {
        SweepExpired();
        var items = _byId.Values.Select(e => new WorldGroundItemView(e.GroundItemId, e.X, e.Y, e.Content, e.CreatedAtUtc, e.ExpiresAtUtc)).ToArray();
        return new WorldGroundItemSnapshot(MapId, Epoch, items);
    }
}
