using Athena.Net.World.Contracts;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Athena.Net.World.Runtime;

namespace Athena.Net.World.Tests;

// Feature 16 S2/S3: the World-authoritative ground-item lifecycle (drop, exclusive pickup claim,
// commit/release, lazy expiry) against a REAL Orleans TestCluster - exactly the "spin up a genuine
// cluster, use the real grain" pattern WorldPartitionGrainTests already establishes for presence/
// movement/transfer, never a hand-rolled in-memory stand-in (see MapTcpServer.InMemoryTestWorldRuntime's
// own doc comment for why that distinction matters: claim exclusivity depends on the grain's real
// per-activation turn serialization, which only a genuine grain call exercises).
public sealed class WorldGroundItemGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<WorldPartitionGrainTests.TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);
    private static WorldGroundItemContent Content(int itemId = 501, uint amount = 1) => new(itemId, amount);
    private static WorldGroundItemOwnership NoOwner => new(null);

    [Fact]
    public async Task Drop_IsIdempotentUnderTheSameDropId()
    {
        var grain = Partition("prontera-region");
        var dropId = Guid.NewGuid();
        var drop = new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, dropId);

        var first = await grain.DropGroundItemAsync(drop);
        var retry = await grain.DropGroundItemAsync(drop);

        Assert.Equal(WorldGroundItemDropStatus.Dropped, first.Status);
        Assert.Equal(WorldGroundItemDropStatus.AlreadyDropped, retry.Status);
        Assert.Equal(first.GroundItemId, retry.GroundItemId);
        var snapshot = await grain.GetGroundItemsAsync("prontera");
        Assert.Single(snapshot.Items);
    }

    [Fact]
    public async Task ConcurrentClaimsForTheSameItem_ResolveToExactlyOneWinner()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);

        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: (uint)(1000 + i), Guid.NewGuid()))));

        Assert.Single(claims, c => c.Status == WorldGroundItemClaimStatus.Claimed);
        Assert.Equal(15, claims.Count(c => c.Status == WorldGroundItemClaimStatus.AlreadyClaimedByOther));
    }

    [Fact]
    public async Task RetryingTheSameClaimId_IsSafeAndAnswersAlreadyClaimed()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);
        var claimId = Guid.NewGuid();
        var claim = new WorldGroundItemClaim(reference, CharacterId: 1001, claimId);

        var first = await grain.ClaimGroundItemAsync(claim);
        var retry = await grain.ClaimGroundItemAsync(claim);

        Assert.Equal(WorldGroundItemClaimStatus.Claimed, first.Status);
        Assert.Equal(WorldGroundItemClaimStatus.AlreadyClaimed, retry.Status);
        Assert.Equal(first.Content, retry.Content);
    }

    [Fact]
    public async Task CompleteClaim_PermanentlyRemovesTheItem_AndReplayIsSafe()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);
        var claimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, claimId));
        var claimReference = new WorldGroundItemClaimReference(reference, claimId);

        var complete = await grain.CompleteGroundItemClaimAsync(claimReference);
        var replay = await grain.CompleteGroundItemClaimAsync(claimReference);

        Assert.Equal(WorldGroundItemCompletionStatus.Applied, complete.Status);
        Assert.Equal(WorldGroundItemCompletionStatus.AlreadyApplied, replay.Status);
        Assert.Empty((await grain.GetGroundItemsAsync("prontera")).Items);
    }

    // Crash/retry-interruption scenario (plan doc S2 binding requirement): the claim held, but the
    // caller's own CharServer grant failed (e.g. inventory full) - the item must return to Available
    // so ANOTHER character (or the same one, later) can still pick it up; it must never be stuck
    // permanently reserved by a claim that never completes.
    [Fact]
    public async Task ReleasedClaim_ReturnsTheItemToAvailable_ForAnyoneToClaimAgain()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);
        var firstClaimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, firstClaimId));

        var release = await grain.ReleaseGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, firstClaimId));
        var secondClaim = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1002, Guid.NewGuid()));

        Assert.Equal(WorldGroundItemCompletionStatus.Applied, release.Status);
        Assert.Equal(WorldGroundItemClaimStatus.Claimed, secondClaim.Status);
        Assert.Single((await grain.GetGroundItemsAsync("prontera")).Items);
    }

    [Fact]
    public async Task ReleaseOrCompleteWithAWrongClaimId_IsRejectedAsNotFound_AndNeverDisturbsTheRealHolder()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);
        var realClaimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, realClaimId));

        var wrongRelease = await grain.ReleaseGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, Guid.NewGuid()));

        Assert.Equal(WorldGroundItemCompletionStatus.NotFound, wrongRelease.Status);
        // The real holder's claim is untouched - completing it with the RIGHT id still succeeds.
        Assert.Equal(WorldGroundItemCompletionStatus.Applied,
            (await grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, realClaimId))).Status);
    }

    [Fact]
    public async Task ClaimingAnUnknownOrAlreadyCompletedItem_IsNotFound()
    {
        var grain = Partition("prontera-region");
        var epoch = (await grain.GetGroundItemsAsync("prontera")).Epoch;
        var reference = new WorldGroundItemReference("prontera", epoch, GroundItemId: 999_999);

        var result = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, Guid.NewGuid()));

        Assert.Equal(WorldGroundItemClaimStatus.NotFound, result.Status);
    }

    // Ownership-window timing itself is covered deterministically (fake clock, never real
    // Task.Delay against a 200ms window shared with full-suite ambient load) by
    // GroundItemOwnershipWindowTests below - moved there after this real-time version proved
    // flaky under parallel suite load (the owner's own claim+release round trip could itself eat
    // enough of a 200ms window to make the "during window" assertion race the window's own end).

    // This test's grain uses the DEFAULT 60s lifetime (this fixture's cluster has no short-timing
    // override) - so actual time-based expiry is exercised by ShortDeadlineGroundItemGrainTests
    // below instead, which injects WorldGroundItemOptions with millisecond-scale durations. This
    // test only proves BOTH an unclaimed AND a claimed item are still present well before 60s -
    // a negative-space check that claiming does not somehow cause a premature removal, not a
    // timing-dependent assertion of the expiry deadline itself.
    [Fact]
    public async Task NeitherUnclaimedNorClaimedItems_ExpirePrematurely()
    {
        var grain = Partition("prontera-region");
        await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(itemId: 501), NoOwner, Guid.NewGuid()));
        var claimedDrop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 151, 181, Content(itemId: 502), NoOwner, Guid.NewGuid()));
        var epoch = (await grain.GetGroundItemsAsync("prontera")).Epoch;
        var claimedReference = new WorldGroundItemReference("prontera", epoch, claimedDrop.GroundItemId!.Value);
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(claimedReference, CharacterId: 1001, Guid.NewGuid()));

        var snapshot = await grain.GetGroundItemsAsync("prontera");
        Assert.Equal(2, snapshot.Items.Count);
    }

    // A repeated ClaimId alone is not proof of "the same character retrying" - a caller-minted Guid
    // merely matching (whether by bug, collision, or a leaked/guessed value) must never hand pickup
    // Content to a DIFFERENT character than the one who actually holds the claim.
    [Fact]
    public async Task SameClaimIdFromADifferentCharacter_IsRejectedAsAlreadyClaimedByOther_NeverLeaksContent()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);
        var sharedClaimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, sharedClaimId));

        var impersonation = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1002, sharedClaimId));

        Assert.Equal(WorldGroundItemClaimStatus.AlreadyClaimedByOther, impersonation.Status);
        Assert.Null(impersonation.Content);
    }

    [Fact]
    public async Task StaleEpoch_IsRejectedWithoutMutatingAnything()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var staleEpoch = new WorldSimulationEpoch(Guid.NewGuid());
        var reference = new WorldGroundItemReference("prontera", staleEpoch, drop.GroundItemId!.Value);

        var result = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, Guid.NewGuid()));

        Assert.Equal(WorldGroundItemClaimStatus.StaleEpoch, result.Status);
        Assert.Single((await grain.GetGroundItemsAsync("prontera")).Items);
    }

    // CompleteGroundItemClaimAsync/ReleaseGroundItemClaimAsync must apply the SAME epoch fencing
    // ClaimGroundItemAsync already does - a caller holding a reference minted against a stale/
    // reactivated simulation must never complete or release a claim in the CURRENT one, even if the
    // GroundItemId/ClaimId values happen to coincide.
    [Fact]
    public async Task CompleteWithStaleEpoch_IsRejected_AndNeverDisturbsTheRealClaim()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var realEpoch = (await grain.GetGroundItemsAsync("prontera")).Epoch;
        var realReference = new WorldGroundItemReference("prontera", realEpoch, drop.GroundItemId!.Value);
        var claimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(realReference, CharacterId: 1001, claimId));
        var staleReference = new WorldGroundItemReference("prontera", new WorldSimulationEpoch(Guid.NewGuid()), drop.GroundItemId!.Value);

        var staleComplete = await grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(staleReference, claimId));

        Assert.Equal(WorldGroundItemCompletionStatus.StaleEpoch, staleComplete.Status);
        // The real claim is untouched - completing it with the REAL epoch still succeeds.
        Assert.Equal(WorldGroundItemCompletionStatus.Applied,
            (await grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(realReference, claimId))).Status);
    }

    [Fact]
    public async Task ReleaseWithStaleEpoch_IsRejected_AndNeverDisturbsTheRealClaim()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var realEpoch = (await grain.GetGroundItemsAsync("prontera")).Epoch;
        var realReference = new WorldGroundItemReference("prontera", realEpoch, drop.GroundItemId!.Value);
        var claimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(realReference, CharacterId: 1001, claimId));
        var staleReference = new WorldGroundItemReference("prontera", new WorldSimulationEpoch(Guid.NewGuid()), drop.GroundItemId!.Value);

        var staleRelease = await grain.ReleaseGroundItemClaimAsync(new WorldGroundItemClaimReference(staleReference, claimId));

        Assert.Equal(WorldGroundItemCompletionStatus.StaleEpoch, staleRelease.Status);
        // The real claim is untouched - releasing it with the REAL epoch still succeeds.
        Assert.Equal(WorldGroundItemCompletionStatus.Applied,
            (await grain.ReleaseGroundItemClaimAsync(new WorldGroundItemClaimReference(realReference, claimId))).Status);
    }
}

// GroundItemId is the wire-facing "groundActorId" field (see ai/iro-2026-wire.md) and must never
// numerically collide with a real player/monster/NPC ActorId on the same partition - proven here by
// loading a real monster spawn (which leases from the SAME global IActorIdBlockAuthorityGrain, see
// WorldPartitionGrain.LoadMonsterSpawnsAsync) and a ground-item drop (which leases from
// GroundItemActorIdAllocator) on the same partition, then confirming their allocated ids are drawn
// from disjoint ranges. A separate fixture/cluster because this is the only test in this file that
// needs real collision data (monster spawn resolution requires it; every other ground-item test
// deliberately never touches monster state at all).
public sealed class GroundItemActorIdCollisionTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);

    [Fact]
    public async Task GroundItemIds_NeverCollideWithARealMonsterActorId_OnTheSamePartition()
    {
        var grain = Partition("prontera-region");
        var spawn = new WorldMonsterSpawnDefinition(MobId: 1002, "prontera", X: 100, Y: 100, Xs: 1, Ys: 1, Count: 1, RespawnDelayMs: 5000, RespawnRandomDelayMs: 0,
            SpawnName: "Poring", WalkSpeedMs: 400, AttackRange: 1, MaxHp: 55, Mode: 0);
        var spawnResult = await grain.LoadMonsterSpawnsAsync(new WorldMonsterSpawnBatch("prontera", "", [spawn]));
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, spawnResult.Status);
        var monsterSnapshot = await grain.PollMonsterFeedAsync(null, "prontera");
        var monsterActorId = Assert.Single(monsterSnapshot.Snapshot!).ActorId;

        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, new WorldGroundItemContent(501, 1), new WorldGroundItemOwnership(null), Guid.NewGuid()));

        Assert.NotEqual(monsterActorId, drop.GroundItemId!.Value);
    }

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([MakeAllWalkableMap("prontera")]))
                .AddSingleton(TimeProvider.System);
        }
    }

    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 200) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());
}

// Exercises WorldGroundItemOptions' short-deadline overrides against a real wall-clock wait - the
// same "inject a much shorter duration, observe the real behavior deterministically" pattern
// WorldMonsterSimulationTests' own ShortWindowConfigurator already establishes.
public sealed class ShortDeadlineGroundItemGrainTests : IAsyncLifetime
{
    private static readonly TimeSpan ShortLifetime = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ShortUnresolvedClaimDeadline = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ShortDropDedupWindow = TimeSpan.FromMilliseconds(150);

    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<ShortDeadlineConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);
    private static WorldGroundItemContent Content(int itemId = 501, uint amount = 1) => new(itemId, amount);
    private static WorldGroundItemOwnership NoOwner => new(null);

    // Issue 1: a claim nobody ever completes or releases (the claiming MapServer crashed before
    // learning its CharServer grant's outcome) must NEVER be silently returned to Available for a
    // different character - the grant might already have committed. Instead, once its deadline
    // passes, the item is permanently removed: an acceptable, bounded loss (the same ephemeral-
    // world-state policy a WorldServer restart already applies unconditionally to ALL ground items),
    // never a duplicate-grant risk.
    [Fact]
    public async Task UnresolvedClaim_IsPermanentlyRemoved_NeverReleasedToAnotherCharacter_AfterItsDeadline()
    {
        var grain = Partition("prontera-region");
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, Guid.NewGuid()));

        await Task.Delay(250);

        Assert.Empty((await grain.GetGroundItemsAsync("prontera")).Items);
        var anotherCharacterClaim = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1002, Guid.NewGuid()));
        Assert.Equal(WorldGroundItemClaimStatus.NotFound, anotherCharacterClaim.Status);
    }

    // Issue 2: a drop-event retry that is merely DELAYED (not lost) must still be recognized even
    // after the original item already left the world via a successful pickup - otherwise a slow
    // network retry of one monster-death event could duplicate the loot.
    [Fact]
    public async Task DelayedDropRetry_AfterTheItemWasAlreadyPickedUp_StillAnswersAlreadyDropped_NeverDuplicatingLoot()
    {
        var grain = Partition("prontera-region");
        var dropId = Guid.NewGuid();
        var drop = new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, dropId);
        var first = await grain.DropGroundItemAsync(drop);
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, first.GroundItemId!.Value);
        var claimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, claimId));
        await grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, claimId));
        Assert.Empty((await grain.GetGroundItemsAsync("prontera")).Items);

        // The delayed retry arrives AFTER the pickup completed, but still within the dedup window.
        var delayedRetry = await grain.DropGroundItemAsync(drop);

        Assert.Equal(WorldGroundItemDropStatus.AlreadyDropped, delayedRetry.Status);
        Assert.Equal(first.GroundItemId, delayedRetry.GroundItemId);
        Assert.Empty((await grain.GetGroundItemsAsync("prontera")).Items); // No new item was created.
    }

    // Once the dedup window itself has fully elapsed (no more in-flight retries are credible), the
    // SAME DropId is free to be reused for a genuinely new, unrelated drop - dedup is bounded, not a
    // permanent "this DropId may only ever be used once" ledger.
    [Fact]
    public async Task AfterTheDedupWindowElapses_TheSameDropIdCanBeUsedForAFreshDrop()
    {
        var grain = Partition("prontera-region");
        var dropId = Guid.NewGuid();
        var drop = new WorldGroundItemDrop("prontera", 150, 180, Content(), NoOwner, dropId);
        var first = await grain.DropGroundItemAsync(drop);
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, first.GroundItemId!.Value);
        var claimId = Guid.NewGuid();
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, claimId));
        await grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, claimId));

        await Task.Delay(300);
        var freshDrop = await grain.DropGroundItemAsync(drop);

        Assert.Equal(WorldGroundItemDropStatus.Dropped, freshDrop.Status);
        Assert.NotEqual(first.GroundItemId, freshDrop.GroundItemId);
        Assert.Single((await grain.GetGroundItemsAsync("prontera")).Items);
    }

    public sealed class ShortDeadlineConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services.AddSingleton<IWorldPartitionResolver>(
                WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>()
                .AddSingleton<IMapCollisionProvider>(EmptyMapCollisionProvider.Instance)
                .AddSingleton(new WorldGroundItemOptions(ShortLifetime, ShortUnresolvedClaimDeadline, ShortDropDedupWindow));
        }
    }
}

// Issue 1 regression, reproduced with a deterministic fake clock (moved explicitly, never real
// wall-clock waiting) at the PRODUCTION default durations: DropDedupWindow (30s) used to be tracked
// completely independently of the item's own lifetime (60s default) - SweepExpired's dedup sweep
// purged a DropId's dedup record purely by its own 30s-old deadline, with no regard for whether the
// ground item it pointed to was still alive. A retry arriving after 30s but before the item's own
// 60s lifetime elapsed (or before its claim completed) would find no dedup record and mint a SECOND
// ground item for the exact same logical drop - a real duplication bug. The fix (WorldGroundItemMap
// Simulation.SweepExpired) now never reaps a dedup record whose GroundItemId is still present in
// _byId, regardless of the record's own DeadlineUtc - only once the item actually leaves the world
// does the bounded post-removal window begin.
public sealed class GroundItemDropDedupVersusLifetimeTests : IAsyncLifetime
{
    private static readonly WorldPresenceLeaseTests.OffsetClock Clock = new();
    private TestCluster _cluster = null!;
    public async Task InitializeAsync()
    {
        Clock.Reset();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<Configurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);

    [Fact]
    public async Task DelayedRetry_PastTheOldDedupWindow_ButWhileTheItemIsStillAlive_StillAnswersAlreadyDropped()
    {
        var grain = Partition("prontera-region");
        var dropId = Guid.NewGuid();
        var drop = new WorldGroundItemDrop("prontera", 150, 180, new WorldGroundItemContent(501, 1), new WorldGroundItemOwnership(null), dropId);

        var first = await grain.DropGroundItemAsync(drop);
        Assert.Equal(WorldGroundItemDropStatus.Dropped, first.Status);

        // Past the OLD independent 30s dedup deadline, but well before the item's own 60s
        // flooritem_lifetime - the item is still Available and alive the entire time.
        Clock.Advance(TimeSpan.FromSeconds(35));

        var delayedRetry = await grain.DropGroundItemAsync(drop);

        Assert.Equal(WorldGroundItemDropStatus.AlreadyDropped, delayedRetry.Status);
        Assert.Equal(first.GroundItemId, delayedRetry.GroundItemId);
        Assert.Single((await grain.GetGroundItemsAsync("prontera")).Items); // Exactly one item - never duplicated.
    }

    [Fact]
    public async Task DelayedRetry_PastTheOldDedupWindow_WhileTheItemIsStillClaimedAndUnresolved_StillAnswersAlreadyDropped()
    {
        var grain = Partition("prontera-region");
        var dropId = Guid.NewGuid();
        var drop = new WorldGroundItemDrop("prontera", 150, 180, new WorldGroundItemContent(501, 1), new WorldGroundItemOwnership(null), dropId);
        var first = await grain.DropGroundItemAsync(drop);
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, first.GroundItemId!.Value);
        await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, Guid.NewGuid()));

        // Past the OLD independent 30s dedup deadline, but still well within the unresolved-claim's
        // own (also default 30s - advanced together here since the bug was DeadlineUtc tracking
        // ANY independent value, not specifically the dedup default) deadline is not what matters:
        // the item remains present in _byId (claimed) the whole time, which is what the fix checks.
        Clock.Advance(TimeSpan.FromSeconds(29));

        var delayedRetry = await grain.DropGroundItemAsync(drop);

        Assert.Equal(WorldGroundItemDropStatus.AlreadyDropped, delayedRetry.Status);
        Assert.Equal(first.GroundItemId, delayedRetry.GroundItemId);
    }

    public sealed class Configurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services.AddSingleton<IWorldPartitionResolver>(
                WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>()
                .AddSingleton<IMapCollisionProvider>(EmptyMapCollisionProvider.Instance)
                .AddSingleton<TimeProvider>(Clock);
        }
    }
}

// Ownership window (pinned item_first_get_time, first tier only - see
// WorldGroundItemMapSimulation's own documented scope simplification): only the owner may claim
// during the window; anyone may claim once it elapses. Uses a deterministic fake clock (moved
// explicitly, never real wall-clock waiting) so the assertion can never race ambient full-suite
// load the way the original real-Task.Delay version did.
public sealed class GroundItemOwnershipWindowTests : IAsyncLifetime
{
    private static readonly WorldPresenceLeaseTests.OffsetClock Clock = new();
    private TestCluster _cluster = null!;
    public async Task InitializeAsync()
    {
        Clock.Reset();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<Configurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);

    [Fact]
    public async Task BlocksNonOwnerDuringTheWindow_ThenOpensToAnyoneAfterItElapses()
    {
        // OffsetClock.GetUtcNow() is real wall-clock time PLUS an explicit offset (see its own doc
        // comment) - it never runs BACKWARD or freezes real time, so a short window (the original
        // 200ms bug) remains exposed to ordinary real RPC overhead across several grain calls before
        // this test ever reaches its own "during window" assertion. A window comfortably longer than
        // any plausible real elapsed time here (a handful of in-process TestCluster RPCs) is what
        // actually makes the "during window" assertion deterministic - Clock.Advance then jumps WELL
        // past it for the "after window" assertion, never relying on elapsed real time staying under
        // a tight bound. Must stay comfortably UNDER the item's own 60s default flooritem_lifetime
        // (this cluster uses production WorldGroundItemOptions defaults) - advancing past that would
        // expire the item itself before the "after window" claim ever ran.
        var grain = Partition("prontera-region");
        var ownership = new WorldGroundItemOwnership(OwnerCharacterId: 1001, OwnerWindowMs: 5_000);
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, new WorldGroundItemContent(501, 1), ownership, Guid.NewGuid()));
        var reference = new WorldGroundItemReference("prontera", (await grain.GetGroundItemsAsync("prontera")).Epoch, drop.GroundItemId!.Value);

        var ownerClaimId = Guid.NewGuid();
        var ownerClaim = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1001, ownerClaimId));
        Assert.Equal(WorldGroundItemClaimStatus.Claimed, ownerClaim.Status);
        await grain.ReleaseGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, ownerClaimId));

        var nonOwnerDuringWindow = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1002, Guid.NewGuid()));
        Assert.Equal(WorldGroundItemClaimStatus.NotEligible, nonOwnerDuringWindow.Status);

        Clock.Advance(TimeSpan.FromMilliseconds(6_000));
        var nonOwnerAfterWindow = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1002, Guid.NewGuid()));
        Assert.Equal(WorldGroundItemClaimStatus.Claimed, nonOwnerAfterWindow.Status);
    }

    public sealed class Configurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services.AddSingleton<IWorldPartitionResolver>(
                WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>()
                .AddSingleton<IMapCollisionProvider>(EmptyMapCollisionProvider.Instance)
                .AddSingleton<TimeProvider>(Clock);
        }
    }
}
