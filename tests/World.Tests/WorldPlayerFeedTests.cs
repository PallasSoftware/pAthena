using Athena.Net.World.Contracts;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// Item 14 phases 1-2: the World-owned player feed (WorldPlayerMapSimulation behind
// WorldPartitionGrain) - atomic bootstrap, ordering, idempotency, stale-identity rejection,
// epoch/cursor/retention resync and lifecycle entries - exercised through the real grain on a real
// Orleans TestCluster, never a fake.
public sealed class WorldPlayerFeedTests : IAsyncLifetime
{
    private const string RestPartition = "world-rest";
    private const string Map = "izlude";

    private TestCluster _cluster = null!;
    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<WorldPartitionGrainTests.TopologyConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id = RestPartition) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);
    private static WorldPlayerPresence Presence(uint characterId, string map = Map, ushort x = 150, ushort y = 180) =>
        new(Guid.NewGuid(), ActorId: characterId + 1000, characterId, map, x, y);
    private static WorldPlayerPublicState State(string name = "Test", byte direction = 0) =>
        new(name, direction, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static async Task<WorldPlayerFeedPage> BootstrapAsync(IWorldPartitionGrain grain, string map = Map) => await grain.PollPlayerFeedAsync(null, map);
    private static WorldPlayerFeedCursor CursorOf(WorldPlayerFeedPage page) => new(page.Epoch, page.AsOfSequence);

    [Fact]
    public async Task NullCursor_ReturnsAtomicSnapshot_AndAnImmediateIncrementalPollIsEmpty()
    {
        var grain = Partition();
        var a = Presence(1);
        var b = Presence(2);
        await grain.RegisterPresenceAsync(a, State("A"));
        await grain.RegisterPresenceAsync(b, State("B"));

        var page = await BootstrapAsync(grain);

        Assert.Equal(WorldPlayerFeedStatus.Ready, page.Status);
        Assert.Null(page.Entries);
        Assert.Equal(["A", "B"], page.Snapshot!.OrderBy(e => e.Presence.CharacterId).Select(e => e.PublicState.CharacterName));
        Assert.Equal(2, page.AsOfSequence);

        var next = await grain.PollPlayerFeedAsync(CursorOf(page), Map);
        Assert.Equal(WorldPlayerFeedStatus.Ready, next.Status);
        Assert.Null(next.Snapshot);
        Assert.Empty(next.Entries!);
        Assert.Equal(page.AsOfSequence, next.AsOfSequence);
    }

    [Fact]
    public async Task LifecycleEntries_AreGaplessMonotonicAndInCommitOrder()
    {
        var grain = Partition();
        var baseline = await BootstrapAsync(grain);
        var cursor = CursorOf(baseline);
        var p = Presence(1);
        await grain.RegisterPresenceAsync(p, State("Walker"));
        await grain.UpdatePlayerLookAsync(p.CharacterId, p.PresenceId, direction: 3, headDirection: 1);
        await grain.UpdatePlayerPublicStateAsync(p.CharacterId, p.PresenceId, State("Walker") with { HairStyle = 9 });

        var move = await grain.MovePlayerAsync(new WorldMovementCommand(p.PresenceId, p.CharacterId, Map, 150, 180, 152, 181));
        Assert.Equal(WorldMovementStatus.Moved, move.Status);
        var movementId = move.MovementId!.Value;
        // MovePlayerAsync alone publishes nothing - only the confirmation does (a pre-truncation
        // destination can never leak to a remote replica).
        Assert.Equal(WorldPlayerFeedEntryKind.LookChanged, (await grain.PollPlayerFeedAsync(cursor, Map)).Entries![^1].Kind);
        await grain.ConfirmMovementProjectionAsync(new WorldMovementProjectionConfirmation(movementId, p.PresenceId, p.CharacterId, Map, 777));
        await grain.AdvanceMovementAsync(new WorldMovementAdvance(movementId, p.PresenceId, p.CharacterId, Map, 150, 180, 151, 181));
        await grain.AdvanceMovementAsync(new WorldMovementAdvance(movementId, p.PresenceId, p.CharacterId, Map, 151, 181, 152, 181));
        await grain.UnregisterPresenceAsync(Map, p.CharacterId, p.PresenceId);

        var page = await grain.PollPlayerFeedAsync(cursor, Map);
        var entries = page.Entries!;
        Assert.Equal(
            [WorldPlayerFeedEntryKind.Registered, WorldPlayerFeedEntryKind.LookChanged, WorldPlayerFeedEntryKind.LookChanged,
             WorldPlayerFeedEntryKind.MovementStarted, WorldPlayerFeedEntryKind.Moved, WorldPlayerFeedEntryKind.MovementFinished,
             WorldPlayerFeedEntryKind.Unregistered],
            entries.Select(e => e.Kind));
        Assert.Equal(Enumerable.Range((int)cursor.Sequence + 1, entries.Count).Select(i => (long)i), entries.Select(e => e.Sequence));
        Assert.Equal(page.AsOfSequence, entries[^1].Sequence);

        // Look + public-state updates change the authoritative public state carried by later entries.
        Assert.Equal((byte)3, entries[1].Entry.PublicState.Direction);
        Assert.Equal(9, (int)entries[2].Entry.PublicState.HairStyle);
        Assert.Equal(9, (int)entries[^1].Entry.PublicState.HairStyle); // Unregistered carries the last public state.
        // The confirmed movement carries the caller's tick stamp and World's own destination.
        var started = entries[3].Entry.Movement!;
        Assert.Equal((152, 181), (started.DestinationX, started.DestinationY));
        Assert.Equal(777, started.StartedAtLocalTickStamp);
        Assert.Equal(WorldPlayerFeedEntryKind.Unregistered, entries[^1].Kind);
        Assert.Null(entries[^1].Entry.Movement); // the walk finished before the player left.
    }

    [Fact]
    public async Task ReplayedRegistration_IsIdempotentForConsumers_AndNeverDuplicatesTheSnapshot()
    {
        var grain = Partition();
        var p = Presence(1);
        await grain.RegisterPresenceAsync(p, State("Once"));
        var cursor = CursorOf(await BootstrapAsync(grain));

        var replay = await grain.RegisterPresenceAsync(p, State("Once", direction: 5));
        Assert.Equal(WorldPresenceRegistrationStatus.AlreadyRegistered, replay.Status);

        var page = await grain.PollPlayerFeedAsync(cursor, Map);
        var replayed = Assert.Single(page.Entries!);
        Assert.Equal(WorldPlayerFeedEntryKind.Registered, replayed.Kind);
        Assert.Equal((byte)5, replayed.Entry.PublicState.Direction);
        var snapshot = (await BootstrapAsync(grain)).Snapshot!;
        Assert.Single(snapshot);
    }

    [Fact]
    public async Task StaleOrUnknownIdentity_NeverAppendsAnyFeedEntry()
    {
        var grain = Partition();
        var p = Presence(1);
        await grain.RegisterPresenceAsync(p, State());
        var move = await grain.MovePlayerAsync(new WorldMovementCommand(p.PresenceId, p.CharacterId, Map, 150, 180, 152, 181));
        var movementId = move.MovementId!.Value;
        var before = (await BootstrapAsync(grain)).AsOfSequence;
        var stale = Guid.NewGuid();

        Assert.Equal(WorldPlayerLookUpdateStatus.StalePresence, (await grain.UpdatePlayerLookAsync(p.CharacterId, stale, 1, 1)).Status);
        Assert.Equal(WorldPlayerLookUpdateStatus.NotFound, (await grain.UpdatePlayerLookAsync(999, p.PresenceId, 1, 1)).Status);
        Assert.Equal(WorldPlayerPublicStateUpdateStatus.StalePresence, (await grain.UpdatePlayerPublicStateAsync(p.CharacterId, stale, State("Forged"))).Status);
        Assert.Equal(WorldPresenceUnregistrationStatus.PresenceMismatch, (await grain.UnregisterPresenceAsync(Map, p.CharacterId, stale)).Status);
        Assert.Equal(WorldMovementProjectionStatus.PresenceMismatch,
            (await grain.ConfirmMovementProjectionAsync(new WorldMovementProjectionConfirmation(movementId, stale, p.CharacterId, Map, 1))).Status);
        Assert.Equal(WorldMovementProjectionStatus.SourceMismatch,
            (await grain.ConfirmMovementProjectionAsync(new WorldMovementProjectionConfirmation(Guid.NewGuid(), p.PresenceId, p.CharacterId, Map, 1))).Status);
        Assert.Equal(WorldMovementAdvanceStatus.PresenceMismatch,
            (await grain.AdvanceMovementAsync(new WorldMovementAdvance(movementId, stale, p.CharacterId, Map, 150, 180, 151, 181))).Status);

        var after = await BootstrapAsync(grain);
        Assert.Equal(before, after.AsOfSequence);
        // The forged public state never replaced the real one.
        Assert.Equal("Test", Assert.Single(after.Snapshot!).PublicState.CharacterName);
    }

    [Fact]
    public async Task ConfirmMovementProjection_IsIdempotent_AndMidWalkSnapshotCarriesTheActiveMovement()
    {
        var grain = Partition();
        var p = Presence(1);
        await grain.RegisterPresenceAsync(p, State());
        var move = await grain.MovePlayerAsync(new WorldMovementCommand(p.PresenceId, p.CharacterId, Map, 150, 180, 152, 181));
        var movementId = move.MovementId!.Value;
        var cursor = CursorOf(await BootstrapAsync(grain));
        var confirmation = new WorldMovementProjectionConfirmation(movementId, p.PresenceId, p.CharacterId, Map, 42);

        Assert.Equal(WorldMovementProjectionStatus.Confirmed, (await grain.ConfirmMovementProjectionAsync(confirmation)).Status);
        Assert.Equal(WorldMovementProjectionStatus.AlreadyConfirmed, (await grain.ConfirmMovementProjectionAsync(confirmation)).Status);

        var page = await grain.PollPlayerFeedAsync(cursor, Map);
        Assert.Equal(WorldPlayerFeedEntryKind.MovementStarted, Assert.Single(page.Entries!).Kind);

        // A consumer that bootstraps (or resyncs) mid-walk learns the walk from the snapshot.
        var midWalk = Assert.Single((await BootstrapAsync(grain)).Snapshot!);
        Assert.Equal((152, 181), (midWalk.Movement!.DestinationX, midWalk.Movement.DestinationY));
        Assert.Equal(42, midWalk.Movement.StartedAtLocalTickStamp);

        // Cancelling ends the walk with exactly one MovementFinished and the snapshot goes quiet again.
        var cancel = await grain.CancelMovementAsync(new WorldMovementCancellation(movementId, p.PresenceId, p.CharacterId, Map));
        Assert.Equal(WorldMovementCancellationStatus.Cancelled, cancel.Status);
        Assert.Equal(WorldMovementCancellationStatus.AlreadyAbsent, (await grain.CancelMovementAsync(new WorldMovementCancellation(movementId, p.PresenceId, p.CharacterId, Map))).Status);
        var finished = await grain.PollPlayerFeedAsync(CursorOf(page), Map);
        Assert.Equal(WorldPlayerFeedEntryKind.MovementFinished, Assert.Single(finished.Entries!).Kind);
        Assert.Null(Assert.Single((await BootstrapAsync(grain)).Snapshot!).Movement);
    }

    [Fact]
    public async Task ForeignEpochOrCursorAheadOfWorld_RequiresResyncWithAFreshSnapshot()
    {
        var grain = Partition();
        var p = Presence(1);
        await grain.RegisterPresenceAsync(p, State("Snap"));
        var baseline = await BootstrapAsync(grain);

        var foreignEpoch = await grain.PollPlayerFeedAsync(new WorldPlayerFeedCursor(WorldSimulationEpoch.NewEpoch(), baseline.AsOfSequence), Map);
        Assert.Equal(WorldPlayerFeedStatus.ResyncRequired, foreignEpoch.Status);
        Assert.Equal("Snap", Assert.Single(foreignEpoch.Snapshot!).PublicState.CharacterName);
        Assert.Equal(baseline.Epoch, foreignEpoch.Epoch);
        Assert.Null(foreignEpoch.Entries);

        var ahead = await grain.PollPlayerFeedAsync(new WorldPlayerFeedCursor(baseline.Epoch, baseline.AsOfSequence + 50), Map);
        Assert.Equal(WorldPlayerFeedStatus.ResyncRequired, ahead.Status);
        Assert.NotNull(ahead.Snapshot);
    }

    [Fact]
    public async Task ConsumerFurtherBehindThanRetention_RequiresResync_InsteadOfASilentGap()
    {
        var grain = Partition();
        var p = Presence(1);
        await grain.RegisterPresenceAsync(p, State());
        var stale = CursorOf(await BootstrapAsync(grain));

        // WorldPlayerMapSimulation retains 4096 entries.
        for (var i = 0; i < 4200; i++)
            await grain.UpdatePlayerLookAsync(p.CharacterId, p.PresenceId, (byte)(i % 8), 0);

        var behind = await grain.PollPlayerFeedAsync(stale, Map);
        Assert.Equal(WorldPlayerFeedStatus.ResyncRequired, behind.Status);
        Assert.Equal((byte)(4199 % 8), Assert.Single(behind.Snapshot!).PublicState.Direction);

        // A consumer that re-bootstraps is immediately current again and sees only newer entries.
        var current = CursorOf(await BootstrapAsync(grain));
        await grain.UpdatePlayerLookAsync(p.CharacterId, p.PresenceId, 7, 0);
        var incremental = await grain.PollPlayerFeedAsync(current, Map);
        Assert.Equal(WorldPlayerFeedStatus.Ready, incremental.Status);
        Assert.Single(incremental.Entries!);
    }

    [Fact]
    public async Task FeedsAreIsolatedPerMap_AndSamePartitionTransferEmitsOutThenIn()
    {
        var grain = Partition("prontera-region");
        var p = Presence(1, map: "prontera");
        await grain.RegisterPresenceAsync(p, State("Traveller"));
        var source = CursorOf(await grain.PollPlayerFeedAsync(null, "prontera"));
        var destination = CursorOf(await grain.PollPlayerFeedAsync(null, "prt_fild08d"));

        var result = await grain.TransferPlayerAsync(new WorldTransferCommand(Guid.NewGuid(), p.PresenceId, p.CharacterId, "prontera", "prt_fild08d", 10, 20));
        Assert.Equal(WorldTransferStatus.Completed, result.Status);

        var out_ = Assert.Single((await grain.PollPlayerFeedAsync(source, "prontera")).Entries!);
        var in_ = Assert.Single((await grain.PollPlayerFeedAsync(destination, "prt_fild08d")).Entries!);
        Assert.Equal(WorldPlayerFeedEntryKind.TransferredOut, out_.Kind);
        Assert.Equal(WorldPlayerFeedEntryKind.TransferredIn, in_.Kind);
        Assert.Equal("prt_fild08d", in_.Entry.Presence.MapId);
        Assert.Equal((10, 20), (in_.Entry.Presence.X, in_.Entry.Presence.Y));
        // Public state travels with the player; the source map no longer lists them.
        Assert.Equal("Traveller", in_.Entry.PublicState.CharacterName);
        Assert.Empty((await grain.PollPlayerFeedAsync(null, "prontera")).Snapshot!);
        Assert.Equal("Traveller", Assert.Single((await grain.PollPlayerFeedAsync(null, "prt_fild08d")).Snapshot!).PublicState.CharacterName);
    }
}
