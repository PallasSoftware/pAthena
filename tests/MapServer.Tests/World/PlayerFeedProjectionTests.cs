using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

// Item 14 phases 3-4: PlayerFeedProjection is the ONLY writer of PlayerPresenceRegistry /
// PlayerVisibilityCoordinator. These tests drive it with hand-built World feed shapes (bootstrap
// snapshot, resync snapshot, incremental entries, replays) and assert the exactly-once AOI
// deliveries and idempotency the cross-replica design depends on.
public sealed class PlayerFeedProjectionTests
{
    private const string Map = "izlude";
    private static readonly WorldSimulationEpoch Epoch = WorldSimulationEpoch.NewEpoch();

    private sealed class RecordingObserver(uint actorId) : IPlayerPresenceObserver
    {
        public uint ActorId { get; } = actorId;
        public List<string> Events { get; } = [];
        public Task PlayerEnteredViewAsync(PlayerPresence presence, PlayerEntryKind kind, CancellationToken cancellationToken) { lock (Events) Events.Add($"enter:{presence.ActorId}:{kind}"); return Task.CompletedTask; }
        public Task PlayerMovementChangedAsync(PlayerPresence presence, CancellationToken cancellationToken) { lock (Events) Events.Add($"move:{presence.ActorId}:{presence.X},{presence.Y}"); return Task.CompletedTask; }
        public Task PlayerLookChangedAsync(PlayerPresence presence, CancellationToken cancellationToken) { lock (Events) Events.Add($"look:{presence.ActorId}:{presence.Direction}"); return Task.CompletedTask; }
        public Task PlayerLeftViewAsync(uint actorId, CancellationToken cancellationToken) { lock (Events) Events.Add($"leave:{actorId}"); return Task.CompletedTask; }
        public void ForgetPlayer(uint actorId) { }
    }

    private sealed class Fixture
    {
        public PlayerPresenceRegistry Registry { get; } = new();
        public PlayerVisibilityCoordinator Coordinator { get; }
        public PlayerFeedProjection Projection { get; } = new(Map);
        // A second map's projection sharing the SAME registry/coordinator, as on a real replica.
        public PlayerFeedProjection Geffen { get; } = new("geffen");
        // Local observers by ActorId; any other actor is "remote" and gets the production null observer.
        public Dictionary<uint, RecordingObserver> Local { get; } = [];
        public Fixture() => Coordinator = new PlayerVisibilityCoordinator(Registry);

        public RecordingObserver AddLocal(uint actorId) => Local[actorId] = new RecordingObserver(actorId);
        private IPlayerPresenceObserver Resolve(uint actorId, uint _) => Local.TryGetValue(actorId, out var o) ? o : new NullPlayerPresenceObserver(actorId);

        public Task SnapshotAsync(params WorldPlayerPresenceEntry[] entries) =>
            Projection.ApplySnapshotAsync(entries, Epoch, Registry, Coordinator, Resolve, CancellationToken.None);
        public Task ApplyAsync(WorldPlayerFeedEntry entry) =>
            Projection.ApplyEntryAsync(entry, Registry, Coordinator, Resolve, CancellationToken.None);
        public Task GeffenSnapshotAsync(params WorldPlayerPresenceEntry[] entries) =>
            Geffen.ApplySnapshotAsync(entries, Epoch, Registry, Coordinator, Resolve, CancellationToken.None);
        public Task GeffenApplyAsync(WorldPlayerFeedEntry entry) =>
            Geffen.ApplyEntryAsync(entry, Registry, Coordinator, Resolve, CancellationToken.None);
    }

    private static WorldPlayerPresenceEntry Entry(uint actorId, ushort x, ushort y, byte direction = 0, WorldPlayerMovementProjection? movement = null, string map = Map) =>
        new(new WorldPlayerPresence(Guid.NewGuid(), actorId, actorId + 5000, map, x, y),
            new WorldPlayerPublicState($"P{actorId}", direction, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), movement);

    private static long _sequence;
    private static WorldPlayerFeedEntry Feed(WorldPlayerFeedEntryKind kind, WorldPlayerPresenceEntry entry) =>
        new(Interlocked.Increment(ref _sequence), kind, entry.Presence.ActorId, entry.Presence.CharacterId, entry);

    private static string[] Snapshot(RecordingObserver observer) { lock (observer.Events) return [.. observer.Events]; }

    [Fact]
    public async Task Bootstrap_RegistersEveryPlayer_AndDeliversEachPairEnterExactlyOnce()
    {
        var f = new Fixture();
        var a = f.AddLocal(1);
        var b = f.AddLocal(2);

        await f.SnapshotAsync(Entry(1, 100, 100), Entry(2, 101, 100), Entry(3, 102, 100)); // actor 3 is remote.

        // Registration order is snapshot order: an already-registered viewer sees each later arrival as
        // NewlySpawned (0x09FE); a later arrival discovers the earlier ones as ExistingStandingOrWalking.
        Assert.Equal(["enter:2:NewlySpawned", "enter:3:NewlySpawned"], Snapshot(a).Order().ToArray());
        Assert.Equal(["enter:1:ExistingStandingOrWalking", "enter:3:NewlySpawned"], Snapshot(b).Order().ToArray());
        Assert.True(f.Registry.TryGetByActorId(3, out _)); // remote players are registered for AOI math too.
    }

    [Fact]
    public async Task ResyncSnapshot_IsIdempotent_NoSpuriousLeaveOrReEnter()
    {
        var f = new Fixture();
        var a = f.AddLocal(1);
        var snapshot = new[] { Entry(1, 100, 100), Entry(2, 101, 100) };
        await f.SnapshotAsync(snapshot);
        var before = Snapshot(a);

        await f.SnapshotAsync(snapshot);
        await f.SnapshotAsync(snapshot);

        Assert.Equal(before, Snapshot(a));
    }

    [Fact]
    public async Task ResyncSnapshot_MissingPlayer_IsUnregisteredAndEveryNeighbourSeesExactlyOneLeave()
    {
        var f = new Fixture();
        var a = f.AddLocal(1);
        var c = f.AddLocal(3);
        await f.SnapshotAsync(Entry(1, 100, 100), Entry(2, 101, 100), Entry(3, 102, 100));

        // The player that existed while this replica was behind is gone in the fresh snapshot.
        await f.SnapshotAsync(Entry(1, 100, 100), Entry(3, 102, 100));

        Assert.False(f.Registry.TryGetByActorId(2, out _));
        Assert.Equal(1, Snapshot(a).Count(e => e == "leave:2"));
        Assert.Equal(1, Snapshot(c).Count(e => e == "leave:2"));
        // Re-applying the same snapshot must not repeat the leave.
        await f.SnapshotAsync(Entry(1, 100, 100), Entry(3, 102, 100));
        Assert.Equal(1, Snapshot(a).Count(e => e == "leave:2"));
    }

    [Fact]
    public async Task ReplayedRegisteredAndUnregisteredEntries_CannotDuplicateOrResurrectAPlayer()
    {
        var f = new Fixture();
        var a = f.AddLocal(1);
        await f.SnapshotAsync(Entry(1, 100, 100));
        var two = Entry(2, 101, 100);

        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Registered, two));
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Registered, two)); // replay / AlreadyRegistered retry.
        Assert.Equal(1, Snapshot(a).Count(e => e.StartsWith("enter:2:")));

        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Unregistered, two));
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Unregistered, two)); // replayed departure.
        Assert.Equal(1, Snapshot(a).Count(e => e == "leave:2"));
        Assert.False(f.Registry.TryGetByActorId(2, out _));

        // A late Moved/Look entry for the now-absent player must be ignored, not re-create it.
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Moved, two));
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.LookChanged, two));
        Assert.False(f.Registry.TryGetByActorId(2, out _));
        Assert.Equal(0, Snapshot(a).Count(e => e.StartsWith("move:2") || e.StartsWith("look:2")));
    }

    [Fact]
    public async Task MovementAcrossTheAoiBoundary_ProducesExactlyOneLeaveAndOneReEnter()
    {
        var f = new Fixture();
        var a = f.AddLocal(1);
        await f.SnapshotAsync(Entry(1, 100, 100), Entry(2, 101, 114)); // exactly on the AOI edge (14).
        Assert.Equal(1, Snapshot(a).Count(e => e.StartsWith("enter:2:")));

        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Moved, Entry(2, 101, 115)));
        Assert.Equal(1, Snapshot(a).Count(e => e == "leave:2"));
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Moved, Entry(2, 101, 116))); // still outside: nothing.
        Assert.Equal(1, Snapshot(a).Count(e => e == "leave:2"));

        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.Moved, Entry(2, 101, 114)));
        Assert.Equal(2, Snapshot(a).Count(e => e.StartsWith("enter:2:")));
    }

    [Fact]
    public async Task LookChanged_IsDeliveredToNeighboursAndUpdatesTheRegistryProjection()
    {
        var f = new Fixture();
        var a = f.AddLocal(1);
        await f.SnapshotAsync(Entry(1, 100, 100), Entry(2, 101, 100));

        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.LookChanged, Entry(2, 101, 100, direction: 6)));

        Assert.Contains("look:2:6", Snapshot(a));
        Assert.True(f.Registry.TryGetByActorId(2, out var projected));
        Assert.Equal((byte)6, projected.Direction);
    }

    [Fact]
    public async Task CursorIsOnlyAdvancedByCommitCursor_NeverByApplying()
    {
        var f = new Fixture();
        await f.SnapshotAsync(Entry(1, 100, 100));
        Assert.Null(f.Projection.Cursor);

        f.Projection.CommitCursor(Epoch, 9);

        Assert.Equal(new WorldPlayerFeedCursor(Epoch, 9), f.Projection.Cursor);
    }

    // A player changes map on this replica while the OLD map's feed is not polled (no local session is
    // left there): the destination's TransferredIn must still depart the player from the old map
    // (its neighbours there are told) and introduce it on the new map exactly once - not silently
    // overwrite the stale registration - and the old map's late TransferredOut must then be a no-op.
    [Fact]
    public async Task TransferredIn_WhileTheOldMapIsUnpolled_DepartsOldNeighboursIntroducesNewOnesOnce_AndLateTransferredOutIsIgnored()
    {
        var f = new Fixture();
        var traveller = f.AddLocal(1);
        var oldNeighbour = f.AddLocal(9);
        var newNeighbour = f.AddLocal(5);
        await f.GeffenSnapshotAsync(Entry(1, 100, 100, map: "geffen"), Entry(9, 101, 100, map: "geffen"));
        await f.SnapshotAsync(Entry(5, 100, 100));

        var arrived = Entry(1, 101, 100);
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.TransferredIn, arrived));

        Assert.True(f.Registry.TryGetByActorId(1, out var registered));
        Assert.Equal(Map, registered.MapName);
        Assert.Equal(1, Snapshot(oldNeighbour).Count(e => e == "leave:1"));
        Assert.Equal(1, Snapshot(newNeighbour).Count(e => e.StartsWith("enter:1:")));
        Assert.Equal(1, Snapshot(traveller).Count(e => e.StartsWith("enter:5:")));

        // Replaying the arrival and then the old map's late departure change nothing.
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.TransferredIn, arrived));
        await f.GeffenApplyAsync(Feed(WorldPlayerFeedEntryKind.TransferredOut, Entry(1, 100, 100, map: "geffen")));
        Assert.True(f.Registry.TryGetByActorId(1, out var stillRegistered));
        Assert.Equal(Map, stillRegistered.MapName);
        Assert.Equal(1, Snapshot(newNeighbour).Count(e => e.StartsWith("enter:1:")));
        Assert.Equal(0, Snapshot(newNeighbour).Count(e => e == "leave:1"));
    }

    [Fact]
    public async Task ResyncOfTheOldMap_NoLongerListingAPlayerWhoMoved_DoesNotRemoveTheirNewRegistration()
    {
        var f = new Fixture();
        var newNeighbour = f.AddLocal(5);
        await f.GeffenSnapshotAsync(Entry(1, 100, 100, map: "geffen"));
        await f.SnapshotAsync(Entry(5, 100, 100));
        await f.ApplyAsync(Feed(WorldPlayerFeedEntryKind.TransferredIn, Entry(1, 101, 100)));

        // The old map's feed finally resyncs: the player is no longer there.
        await f.GeffenSnapshotAsync();

        Assert.True(f.Registry.TryGetByActorId(1, out var registered));
        Assert.Equal(Map, registered.MapName);
        Assert.Equal(0, Snapshot(newNeighbour).Count(e => e == "leave:1"));
    }
}
