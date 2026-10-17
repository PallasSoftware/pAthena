using Athena.Net.MapServer.Tests.Gameplay.Rules;
using System.Buffers.Binary;
using System.Reflection;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Presence lifecycle across gateways (ghost players). Real Orleans World, real sessions and sockets, two or
// more independent gateway replicas. Time is controlled by LeaseClock (real time plus an explicit offset), so
// lease expiry is proven by moving the clock, never by sleeping for the TTL.
public sealed partial class CrossReplicaPlayerVisibilityIntegrationTests
{
    // Real time + an explicit offset. Shared by the silo (World grain) and the gateways under test.
    internal sealed class OffsetClock : TimeProvider
    {
        private long _offsetTicks;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
        public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
    }

    internal static readonly OffsetClock LeaseClock = new();

    // A gateway whose process has died: from then on every World call fails like a dead process's would
    // (no unregister, no lease renewal, no polling can reach World).
    public class CrashableRuntime : DispatchProxy
    {
        public IWorldRuntime Inner { get; set; } = null!;
        public volatile bool Crashed;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Crashed && targetMethod!.ReturnType.IsAssignableTo(typeof(Task)))
            {
                var failure = new IOException("Simulated gateway process death.");
                return targetMethod.ReturnType == typeof(Task)
                    ? Task.FromException(failure)
                    : typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]).Invoke(null, [failure]);
            }
            try { return targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
        }
    }

    private sealed record CrashableReplica(Replica Replica, CrashableRuntime Control);

    private CrashableReplica MakeCrashableReplica(string mapId)
    {
        var combatState = new MonsterAttackCadenceStore();
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), TestGameplayRules.Combat());
        var world = new MapServerWorld(WorldMapRegistry.Tutorial, [PoringSpawn(mapId)], combat, EmptyMapCollisionProvider.Instance,
            new UnverifiedGridLineMovementPathProvider(), new MonsterFeedProjectionRegistry(), combatState, rules: TestGameplayRules.Services());
        var orleans = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var proxy = DispatchProxy.Create<IWorldRuntime, CrashableRuntime>();
        var control = (CrashableRuntime)(object)proxy;
        control.Inner = orleans;
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, proxy, LeaseClock);
        return new CrashableReplica(new Replica(world, server, proxy), control);
    }

    private async Task<WorldPlayerFeedPage> WorldPlayersAsync(string mapId) =>
        await _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId)).PollPlayerFeedAsync(null, mapId);

    // The gateway process dies: nothing it does reaches World any more. Its sockets drop, so its sessions end
    // (and their unregister attempts fail, exactly as a dying process could not have reached World either).
    [Fact]
    public async Task GatewayProcessDeath_LeavesTheGhostOnlyUntilItsLeaseLapses_ThenEveryObserverSeesExactlyOneVanish()
    {
        const string mapId = "izlude";
        var gatewayA = MakeCrashableReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(gatewayA.Replica, accountId: 60, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 61, mapId, 101, 100);
        using var _b = b.Client;
        var tickB = () => TickAsync(replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 60), tick: tickB);

        gatewayA.Control.Crashed = true;
        a.Client.Dispose();            // sockets of the dead process.
        // World is unreachable, so every unregister attempt fails: the session must still finish tearing down
        // cleanly (no hang, no exception out of RunAsync) and leave the presence to the lease.
        await WaitForSessionEndAsync(a.RunTask);
        Assert.Equal("client-closed-or-reset", a.Session.EndReason);

        // The ghost: World still holds the presence, and B still shows it, while the dead gateway cannot renew.
        await tickB();
        Assert.Contains((await WorldPlayersAsync(mapId)).Snapshot!, e => e.Presence.ActorId == 60);

        // Time passes (the dead gateway renews nothing; the healthy one keeps renewing through its ticks).
        for (var i = 0; i < 8; i++) { LeaseClock.Advance(TimeSpan.FromSeconds(5)); await tickB(); }

        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 60), tick: tickB);
        await BarrierAsync(b.Stream);
        Assert.Equal(1, HistoryOf(b.Stream).Count(packet => IsPlayerVanish(packet, 60)));
        Assert.DoesNotContain((await WorldPlayersAsync(mapId)).Snapshot!, e => e.Presence.ActorId == 60);
        Assert.False(replicaB.World.Players.TryGetByActorId(60, out _));
        // The healthy observer was never removed by the stale cleanup.
        Assert.Contains((await WorldPlayersAsync(mapId)).Snapshot!, e => e.Presence.ActorId == 61);
    }

    // ------------------------------------------------------------------------------------------------
    // Shared scenario helpers
    // ------------------------------------------------------------------------------------------------

    private static int Entries(Stream stream, uint actorId) => HistoryOf(stream).Count(p => IsPlayerEntry(p, actorId));
    private static int Vanishes(Stream stream, uint actorId) => HistoryOf(stream).Count(p => IsPlayerVanish(p, actorId));

    private static async Task WaitForSessionEndAsync(Task run, TimeSpan? timeout = null)
    {
        var finished = await Task.WhenAny(run, Task.Delay(timeout ?? TimeSpan.FromSeconds(15)));
        Assert.Same(run, finished);
        await run; // surface a faulted RunAsync: a disconnect must end the session cleanly.
    }

    private async Task AssertWorldHoldsExactlyAsync(string mapId, params uint[] actorIds)
    {
        var actors = (await WorldPlayersAsync(mapId)).Snapshot!.Select(e => e.Presence.ActorId).OrderBy(id => id).ToArray();
        Assert.Equal(actorIds.OrderBy(id => id), actors);
    }

    // ------------------------------------------------------------------------------------------------
    // Graceful and abrupt client departures while the gateway stays alive
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NormalLogout_VanishesOnceOnTheObserver_ThenAReconnectIsIntroducedOnceWithNoStaleActor()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 70, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 71, mapId, 101, 100);
        using var _b = b.Client;
        var tickB = () => TickAsync(replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 70), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        await a.Session.StopAsync();     // the full teardown a logout/disconnect runs.
        a.Client.Dispose();
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 70), tick: tickB);
        await AssertWorldHoldsExactlyAsync(mapId, 71);
        Assert.False(replicaB.World.Players.TryGetByActorId(70, out _));

        // The same character logs in again through the same gateway: one valid presence, one introduction each way.
        var a2 = await ConnectAsync(replicaA, accountId: 70, mapId, 99, 100);
        using var _a2 = a2.Client;
        var tickBoth = TickBoth(replicaA, a2.Session, replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 70) && HistoryOf(b.Stream).Count(p => IsPlayerEntry(p, 70)) >= 2, tick: tickBoth);
        await ReadUntilAsync(a2.Stream, packet => IsPlayerIntro(packet, 71), tick: tickBoth);
        for (var i = 0; i < 3; i++) await tickBoth();
        await BarrierAsync(b.Stream);
        await BarrierAsync(a2.Stream);

        Assert.Equal(2, Entries(b.Stream, 70));      // original + the reconnect, never a duplicate.
        Assert.Equal(1, Vanishes(b.Stream, 70));     // exactly one vanish for the logout.
        Assert.Equal(1, Entries(a2.Stream, 71));
        await AssertWorldHoldsExactlyAsync(mapId, 70, 71);
        Assert.True(replicaA.World.Players.TryGetByActorId(70, out _) && replicaB.World.Players.TryGetByActorId(70, out _));
    }

    [Fact]
    public async Task ClientClosesTheGame_SessionEndsCleanly_AndTheObserverSeesOneVanish()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 72, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 73, mapId, 101, 100);
        using var _b = b.Client;
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 72), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        a.Client.Close();   // FIN: the player closed the game client.

        await WaitForSessionEndAsync(a.RunTask);
        Assert.Equal("client-closed-or-reset", a.Session.EndReason);
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 72), tick: () => TickAsync(replicaB, b.Session));
        await BarrierAsync(b.Stream);
        Assert.Equal(1, Vanishes(b.Stream, 72));
        await AssertWorldHoldsExactlyAsync(mapId, 73);
    }

    [Fact]
    public async Task AbruptConnectionReset_EndsTheSession_AndTheObserverSeesOneVanish()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 74, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 75, mapId, 101, 100);
        using var _b = b.Client;
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 74), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        a.Client.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0);   // close sends RST, not FIN.
        a.Client.Dispose();

        await WaitForSessionEndAsync(a.RunTask);
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 74), tick: () => TickAsync(replicaB, b.Session));
        await BarrierAsync(b.Stream);
        Assert.Equal(1, Vanishes(b.Stream, 74));
        await AssertWorldHoldsExactlyAsync(mapId, 75);
    }

    [Fact]
    public void AcceptedClientSockets_AreGivenTcpKeepAlive_SoAVanishedNetworkEndsTheSessionEventually()
    {
        // A network that disappears without FIN/RST cannot be simulated deterministically; what makes the OS
        // fail the stuck read (and so end the session through the normal path) is the keepalive configuration.
        using var client = new System.Net.Sockets.TcpClient();
        MapTcpServer.ConfigureClientSocket(client);
        Assert.NotEqual(0, (int)client.Client.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive)!);
        Assert.Equal(MapTcpServer.KeepAliveTimeSeconds, (int)client.Client.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime)!);
        Assert.Equal(MapTcpServer.KeepAliveIntervalSeconds, (int)client.Client.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval)!);
    }

    [Fact]
    public async Task RepeatedStopAndDispose_AreIdempotent_OneVanish_OneUnregister()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 76, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 77, mapId, 101, 100);
        using var _b = b.Client;
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 76), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        await a.Session.StopAsync();
        await a.Session.StopAsync();
        await a.Session.DisposeAsync();
        a.Client.Dispose();

        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 76), tick: () => TickAsync(replicaB, b.Session));
        for (var i = 0; i < 3; i++) await TickAsync(replicaB, b.Session);
        await BarrierAsync(b.Stream);
        Assert.Equal(1, Vanishes(b.Stream, 76));
        var feedUnregisters = await CommittedPlayerEntriesAsync(mapId, 76, WorldPlayerFeedEntryKind.Unregistered);
        Assert.Equal(1, feedUnregisters);
    }

    // ------------------------------------------------------------------------------------------------
    // Gateway process death, leases, takeover and fencing
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AfterGatewayDeath_TheCharacterCannotReconnectUntilTheLeaseLapses_ThenReconnectsOnAFreshGatewayWithOneValidPresence()
    {
        const string mapId = "izlude";
        var gatewayA = MakeCrashableReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(gatewayA.Replica, accountId: 78, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 79, mapId, 101, 100);
        using var _b = b.Client;
        var tickB = () => TickAsync(replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 78), tick: TickBoth(gatewayA.Replica, a.Session, replicaB, b.Session));
        var deadPresence = a.Session.PresenceId!.Value;

        gatewayA.Control.Crashed = true;
        a.Client.Dispose();
        await Task.WhenAny(a.RunTask, Task.Delay(3000));

        // The restarted MapServer is a NEW gateway process. Until the dead one's lease lapses World still holds
        // its presence, so the character is refused (a duplicate login must not create a second presence).
        var restarted = MakeReplica(mapId);
        var early = await restarted.Runtime.RegisterPresenceAsync(mapId, new WorldPlayerPresence(Guid.NewGuid(), 78, 78, mapId, 99, 100), new WorldPlayerPublicState("P78", 0, 0, 0, 0, 1, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), CancellationToken.None);
        Assert.Equal(WorldPresenceRegistrationStatus.Conflict, early.Status);

        for (var i = 0; i < 4; i++) { LeaseClock.Advance(TimeSpan.FromSeconds(5)); await tickB(); }
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 78), tick: tickB);

        var a2 = await ConnectAsync(restarted, accountId: 78, mapId, 99, 100);
        using var _a2 = a2.Client;
        var tickBoth = TickBoth(restarted, a2.Session, replicaB, b.Session);
        await ReadUntilAsync(a2.Stream, packet => IsPlayerIntro(packet, 79), tick: tickBoth);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 78) && HistoryOf(b.Stream).Count(p => IsPlayerEntry(p, 78)) >= 2, tick: tickBoth);
        await BarrierAsync(b.Stream);

        Assert.Equal(1, Vanishes(b.Stream, 78));
        Assert.Equal(2, Entries(b.Stream, 78));
        var world = (await WorldPlayersAsync(mapId)).Snapshot!.Single(e => e.Presence.ActorId == 78);
        Assert.NotEqual(deadPresence, world.Presence.PresenceId);
        Assert.Equal(a2.Session.PresenceId, world.Presence.PresenceId);
        Assert.Equal(restarted.Runtime.GatewayId, world.Presence.GatewayId);
    }

    [Fact]
    public async Task AfterGatewayDeath_TheCharacterCanReconnectThroughAnotherGateway()
    {
        const string mapId = "izlude";
        var gatewayA = MakeCrashableReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(gatewayA.Replica, accountId: 80, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 81, mapId, 101, 100);
        using var _b = b.Client;
        var tickB = () => TickAsync(replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 80), tick: TickBoth(gatewayA.Replica, a.Session, replicaB, b.Session));
        gatewayA.Control.Crashed = true;
        a.Client.Dispose();
        await Task.WhenAny(a.RunTask, Task.Delay(3000));
        for (var i = 0; i < 4; i++) { LeaseClock.Advance(TimeSpan.FromSeconds(5)); await tickB(); }
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 80), tick: tickB);

        // Same gateway as the observer this time.
        var a2 = await ConnectAsync(replicaB, accountId: 80, mapId, 99, 100);
        using var _a2 = a2.Client;
        var tick = () => TickAsync(replicaB, b.Session, a2.Session);
        await ReadUntilAsync(a2.Stream, packet => IsPlayerIntro(packet, 81), tick: tick);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 80) && HistoryOf(b.Stream).Count(p => IsPlayerEntry(p, 80)) >= 2, tick: tick);
        for (var i = 0; i < 3; i++) await tick();
        await BarrierAsync(b.Stream);
        Assert.Equal(1, Vanishes(b.Stream, 80));
        Assert.Equal(2, Entries(b.Stream, 80));
        await AssertWorldHoldsExactlyAsync(mapId, 80, 81);
    }

    [Fact]
    public async Task StaleSessionDisconnectingAfterANewerRegistration_NeverRemovesTheNewPresence_AndItsRenewalEndsIt()
    {
        const string mapId = "izlude";
        var gatewayA = MakeCrashableReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(gatewayA.Replica, accountId: 82, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 83, mapId, 101, 100);
        using var _b = b.Client;
        var tickB = () => TickAsync(replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 82), tick: TickBoth(gatewayA.Replica, a.Session, replicaB, b.Session));

        // Gateway A freezes (partitioned from World, process still alive): its lease lapses, the character is
        // taken over by a new login on gateway B.
        gatewayA.Control.Crashed = true;
        for (var i = 0; i < 4; i++) { LeaseClock.Advance(TimeSpan.FromSeconds(5)); await tickB(); }
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 82), tick: tickB);
        var a2 = await ConnectAsync(replicaB, accountId: 82, mapId, 99, 100);
        using var _a2 = a2.Client;
        var newPresence = a2.Session.PresenceId!.Value;
        var tick = () => TickAsync(replicaB, b.Session, a2.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 82) && HistoryOf(b.Stream).Count(p => IsPlayerEntry(p, 82)) >= 2, tick: tick);

        // Gateway A reconnects to World. Its stale session's next renewal learns it was superseded and ends; its
        // unregister is fenced by PresenceId and cannot remove the new presence.
        gatewayA.Control.Crashed = false;
        LeaseClock.Advance(WorldPresenceLease.RenewInterval);
        await TickAsync(gatewayA.Replica, a.Session);
        await WaitForSessionEndAsync(a.RunTask);
        Assert.StartsWith("presence-superseded", a.Session.EndReason);
        a.Client.Dispose();

        for (var i = 0; i < 3; i++) await tick();
        await BarrierAsync(b.Stream);
        Assert.Equal(1, Vanishes(b.Stream, 82));   // only the first (lease expiry); the stale unregister added none.
        var world = (await WorldPlayersAsync(mapId)).Snapshot!.Single(e => e.Presence.ActorId == 82);
        Assert.Equal(newPresence, world.Presence.PresenceId);
    }

    [Fact]
    public async Task HealthyGateway_ThatMissedItsRenewals_ReassertsThePlayerWhoIsStillConnected()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 84, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 85, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        var tickB = () => TickAsync(replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 84), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));
        var presenceId = a.Session.PresenceId!.Value;

        // A's gateway stalls (GC pause, partition) for longer than the TTL without dying: World expires the player.
        for (var i = 0; i < 4; i++) { LeaseClock.Advance(TimeSpan.FromSeconds(5)); await tickB(); }
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 84), tick: tickB);
        await AssertWorldHoldsExactlyAsync(mapId, 85);

        // It comes back, finds its player Absent and re-asserts the SAME presence: the still-connected player is restored.
        var tick = TickBoth(replicaA, a.Session, replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 84) && HistoryOf(b.Stream).Count(p => IsPlayerEntry(p, 84)) >= 2, tick: tick);
        var world = (await WorldPlayersAsync(mapId)).Snapshot!.Single(e => e.Presence.ActorId == 84);
        Assert.Equal(presenceId, world.Presence.PresenceId);
        await BarrierAsync(b.Stream);
        Assert.Equal(1, Vanishes(b.Stream, 84));
        Assert.Equal(2, Entries(b.Stream, 84));
    }

    // ------------------------------------------------------------------------------------------------
    // Login orders, late joiners, walking after a reconnect
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ObserverLogsInFirst_SeesNobody_ThenTheOtherLogsInAndBothSeeEachOther_LogsOutAndReconnects_WithoutStaleActors()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var b = await ConnectAsync(replicaB, accountId: 86, mapId, 101, 100);
        using var _b = b.Client;
        for (var i = 0; i < 5; i++) await TickAsync(replicaB, b.Session);
        await BarrierAsync(b.Stream);
        Assert.Empty(HistoryOf(b.Stream).Where(p => IsPlayerEntry(p, 87)));   // character 87 is not logged in: nothing to show.

        var a = await ConnectAsync(replicaA, accountId: 87, mapId, 99, 100);
        var tick = TickBoth(replicaA, a.Session, replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 87), tick: tick);
        await ReadUntilAsync(a.Stream, packet => IsPlayerEntry(packet, 86), tick: tick);

        await a.Session.StopAsync();
        a.Client.Dispose();
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 87), tick: () => TickAsync(replicaB, b.Session));

        var a2 = await ConnectAsync(replicaA, accountId: 87, mapId, 99, 100);
        using var _a2 = a2.Client;
        var tick2 = TickBoth(replicaA, a2.Session, replicaB, b.Session);
        await ReadUntilAsync(a2.Stream, packet => IsPlayerEntry(packet, 86), tick: tick2);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 87) && HistoryOf(b.Stream).Count(p => IsPlayerEntry(p, 87)) >= 2, tick: tick2);
        for (var i = 0; i < 3; i++) await tick2();
        await BarrierAsync(b.Stream);
        await BarrierAsync(a2.Stream);

        Assert.Equal(2, Entries(b.Stream, 87));
        Assert.Equal(1, Vanishes(b.Stream, 87));
        Assert.Equal(1, Entries(a2.Stream, 86));
        Assert.Equal(0, Vanishes(a2.Stream, 86));
    }

    [Fact]
    public async Task LateJoiningGateway_NeverSeesACharacterThatAlreadyLoggedOut_EvenAfterGatewayDeath()
    {
        const string mapId = "izlude";
        var gatewayA = MakeCrashableReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(gatewayA.Replica, accountId: 88, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 89, mapId, 101, 100);
        using var _b = b.Client;
        var tickB = () => TickAsync(replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 88), tick: TickBoth(gatewayA.Replica, a.Session, replicaB, b.Session));
        gatewayA.Control.Crashed = true;
        a.Client.Dispose();
        await Task.WhenAny(a.RunTask, Task.Delay(3000));
        for (var i = 0; i < 4; i++) { LeaseClock.Advance(TimeSpan.FromSeconds(5)); await tickB(); }
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 88), tick: tickB);

        // A gateway that starts only now bootstraps from World's snapshot: the dead character is not in it.
        var replicaC = MakeReplica(mapId);
        var c = await ConnectAsync(replicaC, accountId: 90, mapId, 100, 101);
        using var _c = c.Client;
        var tick = () => TickAsync(replicaC, c.Session);
        await ReadUntilAsync(c.Stream, packet => IsPlayerEntry(packet, 89), tick: tick);
        for (var i = 0; i < 4; i++) await tick();
        await BarrierAsync(c.Stream);
        Assert.Empty(HistoryOf(c.Stream).Where(p => IsPlayerEntry(p, 88)));
        Assert.False(replicaC.World.Players.TryGetByActorId(88, out _));
    }

    [Fact]
    public async Task AfterAReconnect_TheSameCharacterWalksAndTheObserverGetsOneWalkAtTheRightPosition()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 91, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 92, mapId, 101, 100);
        using var _b = b.Client;
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 91), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));
        await a.Session.StopAsync();
        a.Client.Dispose();
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 91), tick: () => TickAsync(replicaB, b.Session));

        var a2 = await ConnectAsync(replicaA, accountId: 91, mapId, 99, 100);
        using var _a2 = a2.Client;
        var tick = TickBoth(replicaA, a2.Session, replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 91) && HistoryOf(b.Stream).Count(p => IsPlayerEntry(p, 91)) >= 2, tick: tick);
        ConsumeQueued(b.Stream, packet => IsPlayerEntry(packet, 91));

        await a2.Stream.WriteAsync(BuildMovementRequest(99, 105));
        await ReadOpcodeAsync(a2.Stream, PacketConstants.ZcNotifyPlayerMove);
        await TickWalkToIdleAsync(replicaB, mapId, 91, 99, 105, tick);
        await BarrierAsync(b.Stream);

        var walks = HistoryOf(b.Stream).Skip(HistoryOf(b.Stream).ToList().FindLastIndex(p => IsPlayerVanish(p, 91)) + 1).Where(p => IsWalkEntry(p, 91)).ToList();
        Assert.Single(walks);
        Assert.Equal((99, 100, 99, 105), DecodeWalk(walks[0]));
        Assert.True(replicaB.World.Players.TryGetByActorId(91, out var tracked));
        Assert.Equal(((ushort)99, (ushort)105), (tracked.X, tracked.Y));
    }

    // A gateway polls a map's player feed only while it has a session on that map. Whatever its registry held
    // when the last local session left therefore goes stale (players leave while nobody here is polling). A
    // client that logs in later must be introduced to the players World has NOW - never to the stale ones that
    // were merely never cleaned up on this idle gateway.
    [Fact]
    public async Task StaleRegistryOnAnIdleGateway_IsNeverReplayedToANewLogin()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 93, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 94, mapId, 101, 100);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 93), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        // B's player leaves: gateway B now has NO session on this map, so it stops polling it.
        await b.Session.StopAsync();
        b.Client.Dispose();
        // Then A leaves, while nobody on gateway B is polling: B's registry still holds A.
        await a.Session.StopAsync();
        a.Client.Dispose();
        await AssertWorldHoldsExactlyAsync(mapId);
        // Gateway B's last session on the map ended, so its projection of that map is evicted: nothing it
        // learned about the map survives to be served to a later login. (The server does this the moment the
        // session ends; the test harness mirrors it asynchronously, so wait for the condition, not a duration.)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (replicaB.World.Players.TryGetByActorId(93, out _) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(replicaB.World.Players.TryGetByActorId(93, out _), "the idle gateway kept a stale registration");
        Assert.True(replicaB.World.PlayerProjectionsOrDefault.TryGet(mapId, out var idleProjection) && idleProjection.Cursor is null);

        // B logs in again on that gateway. A is not online and must never be shown.
        var b2 = await ConnectAsync(replicaB, accountId: 94, mapId, 101, 100);
        using var _b2 = b2.Client;
        var tick = () => TickAsync(replicaB, b2.Session);
        for (var i = 0; i < 5; i++) await tick();
        await BarrierAsync(b2.Stream);

        Assert.Empty(HistoryOf(b2.Stream).Where(p => IsPlayerEntry(p, 93)));   // not even a flash of the ghost...
        Assert.Empty(HistoryOf(b2.Stream).Where(p => IsPlayerVanish(p, 93)));  // ...and so nothing to vanish.
        Assert.False(replicaB.World.Players.TryGetByActorId(93, out _));
    }

    [Fact]
    public async Task MapLoadReplay_IsSkippedWhileTheMapIsNotReconciled_AndTheFeedStillIntroducesEveryoneExactlyOnce()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 95, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 96, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 95), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        // A third player logs in on gateway B while B's registry is, by the gateway's own judgement, not
        // reconciled: the client reports the map loaded and must NOT be handed the registry's contents.
        var reconciled = false;
        var c = await ConnectAsync(replicaB, accountId: 97, mapId, 100, 101, playerMapReconciled: _ => reconciled);
        using var _c = c.Client;
        await BarrierAsync(c.Stream);
        Assert.Empty(HistoryOf(c.Stream).Where(p => IsPlayerEntry(p, 95) || IsPlayerEntry(p, 96)));

        // The ordinary feed (which is reconciling) then introduces the real players, once each.
        reconciled = true;
        var tick = () => TickAsync(replicaB, b.Session, c.Session);
        await ReadUntilAsync(c.Stream, packet => IsPlayerEntry(packet, 95), tick: tick);
        await ReadUntilAsync(c.Stream, packet => IsPlayerEntry(packet, 96), tick: tick);
        for (var i = 0; i < 3; i++) await tick();
        await BarrierAsync(c.Stream);
        Assert.Equal(1, Entries(c.Stream, 95));
        Assert.Equal(1, Entries(c.Stream, 96));
    }

    // StopAsync re-entered from a synchronous cancellation continuation (what the read loop's `finally` does when the
    // cancel runs it inline) must observe the ONE shutdown in flight - never start a second teardown that disposes the
    // gates under the first one. Deterministic: the hook fires exactly where that re-entry happens.
    [Fact]
    public async Task ReentrantStopAsync_DuringTeardown_SharesTheSingleShutdown_AndUnregistersExactlyOnce()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 98, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 99, mapId, 101, 100);
        using var _b = b.Client;
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 98), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        Task? reentrant = null;
        a.Session.DebugAfterSessionCancelledHook = () => { if (reentrant is null) reentrant = a.Session.StopAsync(); };   // once: a regression must fail an assertion, not overflow the stack.
        var outer = a.Session.StopAsync();
        await outer;     // would throw ObjectDisposedException when a second teardown disposed the gates mid-flight.
        a.Client.Dispose();

        Assert.NotNull(reentrant);
        Assert.Same(outer, reentrant);
        await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 98), tick: () => TickAsync(replicaB, b.Session));
        Assert.Equal(1, await CommittedPlayerEntriesAsync(mapId, 98, WorldPlayerFeedEntryKind.Unregistered));
    }
}
