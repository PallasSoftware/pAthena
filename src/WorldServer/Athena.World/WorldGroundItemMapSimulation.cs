using Athena.Net.World.Contracts;

namespace Athena.Net.World;

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
        // null = Available (unclaimed). Set the instant a claim wins; cleared only by Complete/Release.
        public (Guid ClaimId, uint CharacterId)? Claim { get; set; }
    }

    private readonly Dictionary<uint, Entry> _byId = [];
    // DropId -> GroundItemId: lets a retried DropGroundItemAsync call (the SAME logical drop) answer
    // AlreadyDropped instead of minting a second physical item - see WorldGroundItemDrop.DropId's own
    // doc comment. Entries are removed together with their ground item (expiry or claim completion),
    // matching the item's own lifetime - a drop that has already fully left the world (picked up,
    // expired) is eligible to be re-dropped fresh under the SAME DropId without being mistaken for a
    // still-pending duplicate (this is intentional: DropId's purpose is dedup of IN-FLIGHT retries of
    // one drop call, not a permanent "this logical drop may only ever happen once" ledger).
    private readonly Dictionary<Guid, uint> _groundItemIdByDropId = [];
    private uint _nextGroundItemId = 1;

    public string MapId { get; }
    public WorldSimulationEpoch Epoch { get; }
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    public WorldGroundItemMapSimulation(string mapId, TimeProvider timeProvider, TimeSpan? lifetime = null)
    {
        MapId = mapId;
        Epoch = WorldSimulationEpoch.NewEpoch();
        _timeProvider = timeProvider;
        // Pinned flooritem_lifetime battle-conf default (60000ms) - see the plan doc's own "Pinned
        // battle configuration" section. A conf-driven override is future work; no conf plumbing
        // exists for World yet.
        _lifetime = lifetime ?? TimeSpan.FromMilliseconds(60_000);
    }

    // Expiry is lazy/on-access (exactly like WorldPresenceLease's own "World decides expiry on its
    // own clock, on every call that touches the relevant state" convention) - no dedicated timer.
    // A CLAIMED item never expires out from under its holder: the pinned reference has no "the
    // ground item vanished mid-pickup" race, and the window here is always short (one CharServer
    // round trip) - an item only becomes expiry-eligible again if its claim is released.
    private void SweepExpired()
    {
        if (_byId.Count == 0) return;
        var now = _timeProvider.GetUtcNow();
        List<uint>? expired = null;
        foreach (var (id, entry) in _byId)
        {
            if (entry.Claim is not null) continue;
            if (entry.ExpiresAtUtc > now) continue;
            (expired ??= []).Add(id);
        }
        if (expired is null) return;
        foreach (var id in expired) Remove(id);
    }

    private void Remove(uint groundItemId)
    {
        if (!_byId.Remove(groundItemId)) return;
        var dropId = _groundItemIdByDropId.FirstOrDefault(pair => pair.Value == groundItemId, default(KeyValuePair<Guid, uint>));
        if (dropId.Key != Guid.Empty) _groundItemIdByDropId.Remove(dropId.Key);
    }

    public WorldGroundItemDropResult Drop(WorldGroundItemDrop drop)
    {
        SweepExpired();
        if (_groundItemIdByDropId.TryGetValue(drop.DropId, out var existingId) && _byId.ContainsKey(existingId))
            return new(WorldGroundItemDropStatus.AlreadyDropped, existingId);

        var id = _nextGroundItemId++;
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
        _groundItemIdByDropId[drop.DropId] = id;
        return new(WorldGroundItemDropStatus.Dropped, id);
    }

    public WorldGroundItemClaimResult Claim(WorldGroundItemClaim claim)
    {
        SweepExpired();
        if (!_byId.TryGetValue(claim.Item.GroundItemId, out var entry))
            return new(WorldGroundItemClaimStatus.NotFound, null);

        if (entry.Claim is { } existing)
        {
            return existing.ClaimId == claim.ClaimId
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
        return new(WorldGroundItemClaimStatus.Claimed, entry.Content);
    }

    private WorldGroundItemCompletionResult EndClaim(WorldGroundItemClaimReference reference, bool complete)
    {
        if (!_byId.TryGetValue(reference.Item.GroundItemId, out var entry))
            // Not found could mean "never existed" OR "already completed" - both answer the same safe
            // AlreadyApplied/NotFound split the caller cannot usefully distinguish further without
            // keeping a separate tombstone ledger forever; completion already permanently removes the
            // entry, so a replayed Complete after a successful one correctly finds nothing here.
            return new(complete ? WorldGroundItemCompletionStatus.AlreadyApplied : WorldGroundItemCompletionStatus.NotFound);

        if (entry.Claim is not { } held || held.ClaimId != reference.ClaimId)
            return new(WorldGroundItemCompletionStatus.NotFound);

        if (complete) Remove(reference.Item.GroundItemId);
        else entry.Claim = null; // Released: item returns to Available, still subject to its original ownership window/expiry.
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
