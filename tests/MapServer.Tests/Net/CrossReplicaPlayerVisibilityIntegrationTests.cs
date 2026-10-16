using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;
using Athena.Net.World.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.MapServer.Tests.Net;

// Item 14: proves cross-replica player visibility/combat using TWO genuinely independent MapServer
// gateway "worlds" (two separate MapServerWorld/MapTcpServer/OrleansWorldRuntime instances, each
// with its own PlayerPresenceRegistry/PlayerVisibilityCoordinator/PlayerFeedProjectionRegistry/
// MonsterFeedProjectionRegistry - never one shared PlayerVisibilityCoordinator simulating both)
// against ONE shared Orleans TestCluster-hosted IWorldPartitionGrain, exactly the way two real
// MapServer processes share one World silo. Mirrors MapTcpServerMonsterAuthorityIntegrationTests'
// own established TestCluster/ConnectSessionAsync/deterministic-tick pattern.
public sealed partial class CrossReplicaPlayerVisibilityIntegrationTests : IAsyncLifetime
{
    private const int PoringMobId = 1002;
    private const ushort MonsterX = 100;
    private const ushort MonsterY = 100;

    private TestCluster _cluster = null!;
    public async Task InitializeAsync()
    {
        LeaseClock.Reset();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TopologyConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private static IWorldPartitionResolver Resolver() => WorldPartitionTopologyLoader.Load(Path.Combine(FindRepositoryRoot(), "conf", "world_partitions.json"), ["izlude", "geffen"]);

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static MobSpawnDefinition PoringSpawn(string mapId) =>
        new(Athena.Net.MapServer.Generated.GameData.Mobs.GeneratedMobRegistry.Get(PoringMobId), mapId, Count: 1, RespawnDelay: 5000, RespawnRandomDelay: 0,
            new WorldSourceInfo("rAthena", "abc", "test", 0), SpawnName: "Poring", X: (short)MonsterX, Y: (short)MonsterY, Xs: 1, Ys: 1);

    // One independent "gateway replica" - its own MapServerWorld (own PlayerPresenceRegistry/
    // PlayerVisibilityCoordinator/PlayerFeedProjectionRegistry/MonsterFeedProjectionRegistry), own
    // MapTcpServer, own OrleansWorldRuntime instance (a distinct client-side object, though it talks
    // to the SAME underlying TestCluster/grain).
    private sealed record Replica(MapServerWorld World, MapTcpServer Server, IWorldRuntime Runtime);

    private Replica MakeReplica(string mapId)
    {
        var combatState = new MonsterAttackCadenceStore();
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules());
        var world = new MapServerWorld(
            WorldMapRegistry.Tutorial, [PoringSpawn(mapId)], combat,
            EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(),
            new MonsterFeedProjectionRegistry(), combatState);
        // A distinct OrleansWorldRuntime instance per replica - mirrors two genuinely independent
        // MapServer processes each holding their OWN IClusterClient-backed runtime, even though
        // both talk to the SAME underlying TestCluster/grain (_cluster.Client is itself already the
        // one shared cluster client TestCluster exposes for this test's entire process).
        var runtime = new OrleansWorldRuntime(_cluster.Client, Resolver());
        var server = new MapTcpServer(new MapConfigStore(new MapConfig(), "unused.conf"), new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), world, runtime, LeaseClock);
        return new Replica(world, server, runtime);
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask)> ConnectAsync(
        Replica replica, uint accountId, string mapId, ushort x, ushort y, CharacterGameplayState? gameplayState = null, Func<string, bool>? playerMapReconciled = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        var stream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var state = gameplayState ?? new CharacterGameplayState(accountId, 1, 0, 99, 10, 0, 0, 100, 20, 100, 20, 0, 0, 99, 9, 9, 9, 99, 9);
        var session = new MapClientSession(
            (int)accountId, serverClient, connector, iroAuthenticated: true,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            monsterProjections: replica.World.MonsterProjections, combat: replica.World.Combat, combatState: replica.World.CombatState,
            movementPathProvider: replica.World.MovementPathProvider, collisionProvider: replica.World.Collision,
            players: replica.World.Players, playerVisibility: replica.World.PlayerVisibility, visibilityOptions: replica.World.Visibility,
            distributedWorld: replica.Runtime, playerAttackFanout: replica.Server.FanOutPlayerAttackActionAsync, lethalAttackGate: replica.Server.LethalAttackGateForTest);
        replica.Server.RegisterSessionForTest(session); // same table the accept loop uses, so attacker echo fan-out finds it.
        if (playerMapReconciled is not null) session._isPlayerMapReconciled = playerMapReconciled;
        var run = session.RunAsync(CancellationToken.None);
        var auth = new MapAuthOkData(accountId, accountId, 1, 2, 0, 0, false, mapId, x, y, 0, 0, 1, $"P{accountId}", HairStyle: 4, HairColor: 2, ClothesColor: 1);
        await session.CompleteIroAuthenticationAsync(auth);
        await ReadExact(stream, 29);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon
        await ReadExact(stream, 6);  // inventory start
        await ReadExact(stream, 4);  // inventory end
        listener.Stop();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.IsWorldMapEligible && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(session.IsWorldMapEligible);
        return (client, stream, session, run);
    }

    // Drives ONE production tick (monster + player feeds) for the given replica's own session set.
    private static Task TickAsync(Replica replica, params MapClientSession[] sessions) =>
        replica.Server.ProcessOneMonsterTickAsync(sessions, CancellationToken.None);

    // Drives repeated ticks on BOTH replicas until the given condition holds or a bounded timeout
    // elapses - a real per-map tick loop runs continuously in production, so a test proving
    // cross-replica delivery must tolerate needing more than exactly one tick (e.g. a movement/look
    // RPC racing the very next scheduled poll), never assume a single deterministic tick suffices.
    private static async Task TickUntilAsync(Replica replicaA, MapClientSession sessionA, Replica replicaB, MapClientSession sessionB, Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await TickAsync(replicaA, sessionA);
            await TickAsync(replicaB, sessionB);
            await Task.Delay(10);
        }
    }

    private static byte[] BuildMovementRequest(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0x44;
        return packet;
    }

    private static byte[] BuildAttackPacket(uint targetActorId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x0437);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        packet[6] = 7;
        packet[7] = 0x7F;
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    // Per-stream framing state. Every packet is framed once by ReadOnePacketAsync and recorded in
    // arrival order; a packet that does not match the CURRENT predicate stays queued for later reads
    // instead of being discarded, so a mutual-discovery packet that happens to arrive while a test is
    // waiting for something else is never lost. `History` is the full arrival-ordered log, used to
    // assert cross-packet ordering (e.g. lethal action strictly before Died vanish).
    private sealed class Wire
    {
        public readonly List<byte[]> History = [];
        public readonly List<byte[]> Pending = [];
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Stream, Wire> Wires = new();
    private static Wire WireOf(Stream stream) => Wires.GetValue(stream, _ => new Wire());

    private static IReadOnlyList<byte[]> HistoryOf(Stream stream) => WireOf(stream).History;

    // Takes the first packet satisfying `match` (queued ones first, then freshly framed ones) and
    // leaves every non-matching packet queued in order. `tick`, when supplied, runs concurrently with
    // the blocking read for assertions that depend on a real per-map tick loop making repeated
    // progress.
    private static async Task<byte[]> ReadUntilAsync(Stream stream, Func<byte[], bool> match, TimeSpan? timeout = null, Func<Task>? tick = null)
    {
        var wire = WireOf(stream);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        Task? tickLoop = null;
        if (tick is not null)
        {
            tickLoop = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        await tick();
                        await Task.Delay(20, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        try
        {
            while (true)
            {
                var queued = wire.Pending.FindIndex(p => match(p));
                if (queued >= 0)
                {
                    var found = wire.Pending[queued];
                    wire.Pending.RemoveAt(queued);
                    return found;
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Expected packet did not arrive within the timeout. Received so far (arrival order): " + string.Join(' ', wire.History.Select(p => $"{BinaryPrimitives.ReadInt16LittleEndian(p):x4}")));
                byte[] packet;
                try { packet = await ReadOnePacketAsync(stream).WaitAsync(remaining); }
                catch (TimeoutException)
                {
                    throw new TimeoutException("Expected packet did not arrive within the timeout. Received so far (arrival order): " + string.Join(' ', wire.History.Select(p => $"{BinaryPrimitives.ReadInt16LittleEndian(p):x4}[{Convert.ToHexString(p)}]")));
                }
                wire.History.Add(packet);
                wire.Pending.Add(packet);
            }
        }
        finally
        {
            await cts.CancelAsync();
            if (tickLoop is not null)
            {
                try { await tickLoop; } catch { /* best-effort */ }
            }
        }
    }

    // Explicitly consumes queued packets matching `match` (kept in History for ordering assertions).
    // Used to acknowledge, deliberately and by name, traffic that belongs to an earlier phase of a
    // test (e.g. the walk entry received before a leave) so a later wait cannot match it by accident.
    private static IReadOnlyList<byte[]> ConsumeQueued(Stream stream, Func<byte[], bool> match)
    {
        var wire = WireOf(stream);
        var consumed = wire.Pending.Where(match).ToList();
        wire.Pending.RemoveAll(p => match(p));
        return consumed;
    }

    private static Task<byte[]> ReadOpcodeAsync(Stream stream, short opcode, TimeSpan? timeout = null, Func<Task>? tick = null) =>
        ReadUntilAsync(stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == opcode, timeout, tick);

    // Reads one packet by its OWN true length - a fixed-size opcode (0x0087/0x08C8/0x0080 vanish/
    // etc, none of which carry a length field at offset 2) via KnownFixedPacketLength, else the
    // dynamic length-prefixed shape (0x09FE/0x09FF/0x09FD/0x0A30) ReadDynamic already handles.
    // Reading a fixed-size packet through ReadDynamic would misinterpret its own payload bytes at
    // offset 2 as a length prefix - exactly the desync this helper exists to avoid.
    private static async Task<byte[]> ReadOnePacketAsync(Stream stream)
    {
        var header = await ReadExact(stream, 2);
        var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
        var fixedLength = KnownFixedPacketLength(opcode);
        if (fixedLength >= 0) return [.. header, .. await ReadExact(stream, fixedLength - 2)];
        var lengthField = await ReadExact(stream, 2);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthField);
        if (length < 4) throw new InvalidOperationException($"Unframeable packet 0x{opcode:x4}: add its fixed length to KnownFixedPacketLength (length field read as {length}).");
        return [.. header, .. lengthField, .. await ReadExact(stream, length - 4)];
    }

    // Same fixed-opcode table as MapTcpServerMonsterAuthorityIntegrationTests' own
    // KnownFixedPacketLength - -1 means "not fixed, use the dynamic length-prefixed shape instead".
    private static int KnownFixedPacketLength(short opcode) => opcode switch
    {
        (short)PacketConstants.ZcNotifyAct3 => PacketConstants.ZcNotifyAct3Length,
        (short)PacketConstants.ZcHpInfo => PacketConstants.ZcHpInfoLength,
        (short)PacketConstants.ZcStopMove => PacketConstants.ZcStopMoveLength,
        (short)PacketConstants.ZcNotifyVanish => PacketConstants.ZcNotifyVanishLength,
        (short)PacketConstants.ZcNotifyPlayerMove => 12,
        (short)PacketConstants.ZcPlayerInfo => PacketConstants.ZcPlayerInfoLength,
        (short)PacketConstants.ZcPingLive => 2,
        0x009c => 9, // ZC_CHANGE_DIRECTION
        0x0091 => 22, // ZC_NPCACK_MAPMOVE
        0x0b08 => 6,  // inventory list start (map-load bootstrap)
        0x0b0b => 4,  // inventory list end (map-load bootstrap)
        0x01d7 => 15, // self weapon look (map-load bootstrap)
        // Reward/progression tail of a lethal hit (0x00B0/0x0ACB/0x0ACC/0x019B/...): the shared table.
        _ => MapTcpServerMonsterAuthorityIntegrationTests.KnownFixedPacketLength(opcode),
    };

    private static bool IsPlayerIntro(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) is 0x09fe or 0x09ff && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    // Any packet that establishes a player in a viewer's client: stand (0x09FF), spawn (0x09FE) or, for
    // a player that is still walking as it enters view, the walk entry (0x09FD).
    private static bool IsPlayerEntry(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) is 0x09fe or 0x09ff or 0x09fd && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    private static bool IsPlayerVanish(byte[] packet, uint actorId) =>
        packet.Length == 7 && BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyVanish && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)) == actorId;

    private static bool IsAnyMonsterIntro(byte[] packet) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry && packet[4] == 5;

    private static bool IsMonsterDiedVanish(byte[] packet, uint actorId) =>
        IsPlayerVanish(packet, actorId) && packet[6] == PacketConstants.ZcNotifyVanishReasonDied;

    private static bool IsMonsterIntro(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) == (short)PacketConstants.ZcNotifyStandEntry && packet[4] == 5 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    // Point 1-4: A on replica A, B on replica B, same canonical map, mutual discovery.
    [Fact]
    public async Task ReplicaAAndReplicaB_SameMap_DiscoverEachOtherThroughTheSharedWorldFeed()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 1, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 2, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        // Each replica polls the SAME shared World independently - neither has a local socket for
        // the other's player at all, so discovery can ONLY happen through the feed.
        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);

        var aSeesB = await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 2));
        Assert.True(IsPlayerIntro(aSeesB, 2));
        var bSeesA = await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 1));
        Assert.True(IsPlayerIntro(bSeesA, 1));
    }

    // Point 5: movement on replica A is visible to the observer on replica B.
    [Fact]
    public async Task MovementOnReplicaA_IsVisibleToObserverOnReplicaB()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 3, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 4, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 4));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 3));

        await a.Stream.WriteAsync(BuildMovementRequest(99, 102));
        await ReadUntilAsync(a.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x0087);
        await TickAsync(replicaA, a.Session);
        // B's OWN replica poll observes A's MovementStarted entry from the shared World feed.
        await TickAsync(replicaB, b.Session);
        var bSeesAWalk = await ReadUntilAsync(b.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x09fd && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == 3u);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bSeesAWalk.AsSpan(5)));
    }

    // Point 6-7: AOI leave sends exactly one vanish; re-entry sends exactly one entry.
    [Fact]
    public async Task AoiLeaveAndReEntry_EachProduceExactlyOnePacket()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        // A starts exactly at the AOI boundary edge from B (WorldVisibilityOptions.DefaultAreaSize=14,
        // Chebyshev distance) so a SINGLE-cell move - the same mechanism
        // MovementOnReplicaA_IsVisibleToObserverOnReplicaB already proves end-to-end - is enough to
        // cross it, without depending on a long multi-cell real-time walk completing.
        var a = await ConnectAsync(replicaA, accountId: 5, mapId, 101, 114);
        var b = await ConnectAsync(replicaB, accountId: 6, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 6));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 5));

        var tickBoth = TickBoth(replicaA, a.Session, replicaB, b.Session);

        // One cell north: (101,115) is 15 cells from (101,100), outside AreaSize=14.
        await a.Stream.WriteAsync(BuildMovementRequest(101, 115));
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyPlayerMove);
        var bSeesLeave = await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 5), timeout: TimeSpan.FromSeconds(15), tick: tickBoth);
        Assert.True(IsPlayerVanish(bSeesLeave, 5));
        // Packets about A queued BEFORE the leave (its walk entry) belong to the first phase.
        ConsumeQueued(b.Stream, packet => IsPlayerEntry(packet, 5));

        // The return trip must start from a settled walker: wait for the AUTHORITATIVE World state
        // (position reached, no active movement) instead of assuming the walk bookkeeping finished
        // in time - under load a request that arrives mid-walk is a legitimate retarget, whose
        // entry packet is a walk entry rather than a stand entry.
        await WaitForWorldIdleAtAsync(mapId, actorId: 5, x: 101, y: 115, tickBoth);

        // One cell back south re-enters range. The single entry-establishing packet is a stand/spawn
        // entry for an idle player or a walk entry (0x09FD) when the player is still moving as it
        // enters; either way it is exactly ONE entry, followed by no second intro and no vanish.
        await a.Stream.WriteAsync(BuildMovementRequest(101, 114));
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyPlayerMove);
        var bSeesReEntry = await ReadUntilAsync(b.Stream, packet => IsPlayerEntry(packet, 5), timeout: TimeSpan.FromSeconds(15), tick: tickBoth);
        Assert.True(IsPlayerEntry(bSeesReEntry, 5));

        await WaitForWorldIdleAtAsync(mapId, actorId: 5, x: 101, y: 114, tickBoth);
        await tickBoth();
        await BarrierAsync(b.Stream);
        var history = HistoryOf(b.Stream).ToList();
        var firstVanish = history.FindIndex(p => IsPlayerVanish(p, 5));
        var reEntry = history.FindIndex(firstVanish + 1, p => ReferenceEquals(p, bSeesReEntry));
        Assert.True(firstVanish >= 0 && reEntry > firstVanish, "Re-entry must come after the leave.");
        Assert.Equal(1, history.Count(p => IsPlayerVanish(p, 5)));
        // Exactly one entry-establishing packet after the leave: if it was a stand/spawn intro, no
        // second intro follows; if it was a walk entry, no stand/spawn intro follows at all.
        var introsAfterLeave = history.Skip(firstVanish + 1).Count(p => IsPlayerIntro(p, 5));
        Assert.Equal(IsPlayerIntro(bSeesReEntry, 5) ? 1 : 0, introsAfterLeave);
    }

    // The World-authoritative position of the player, with no active movement, is the deterministic
    // "walk finished" signal (the feed snapshot carries the active movement projection).
    private async Task WaitForWorldIdleAtAsync(string mapId, uint actorId, ushort x, ushort y, Func<Task> tick)
    {
        var grain = _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var entry = (await grain.PollPlayerFeedAsync(null, mapId)).Snapshot!.Single(e => e.Presence.ActorId == actorId);
            if (entry.Movement is null && entry.Presence.X == x && entry.Presence.Y == y) return;
            await tick();
            await Task.Delay(20);
        }
        throw new TimeoutException($"World never reported actor {actorId} idle at ({x},{y}).");
    }

    // Point 8: disconnect on replica A becomes a vanish on replica B.
    [Fact]
    public async Task DisconnectOnReplicaA_BecomesVanishOnReplicaB()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 7, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 8, mapId, 101, 100);
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 8));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 7));

        await a.Session.StopAsync();
        a.Client.Dispose();
        await TickAsync(replicaB, b.Session);
        var bSeesVanish = await ReadUntilAsync(b.Stream, packet => IsPlayerVanish(packet, 7));
        Assert.True(IsPlayerVanish(bSeesVanish, 7));
    }

    // Actor-info lookup: a player on replica B can resolve actor info for a player it can see on
    // replica A, purely from the World-fed local PlayerPresenceRegistry (item 14 §9).
    [Fact]
    public async Task ActorInfoLookup_ResolvesAVisibleRemoteReplicaPlayer()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 9, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 10, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 10));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 9));

        var infoRequest = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(infoRequest, 0x0368);
        BinaryPrimitives.WriteUInt32LittleEndian(infoRequest.AsSpan(2), 9);
        infoRequest[6] = 0xe3;
        await b.Stream.WriteAsync(infoRequest);
        var info = await ReadOpcodeAsync(b.Stream, PacketConstants.ZcPlayerInfo);
        Assert.Equal(106, info.Length);
        Assert.Equal((short)0x0a30, BinaryPrimitives.ReadInt16LittleEndian(info));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(2)));
    }

    private static Func<Task> TickBoth(Replica replicaA, MapClientSession sessionA, Replica replicaB, MapClientSession sessionB) =>
        () => TickAsync(replicaA, sessionA).ContinueWith(_ => TickAsync(replicaB, sessionB)).Unwrap();

    // A look (direction) change on replica A reaches the observer on replica B through the World feed.
    [Fact]
    public async Task LookChangeOnReplicaA_IsDeliveredToObserverOnReplicaB()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 15, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 16, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 16), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 15), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        await a.Stream.WriteAsync(new byte[] { 0x61, 0x03, 0x02, 0x00, 0x01, 0x7f });

        var look = await ReadOpcodeAsync(b.Stream, 0x009c, tick: TickBoth(replicaA, a.Session, replicaB, b.Session));
        Assert.Equal(15u, BinaryPrimitives.ReadUInt32LittleEndian(look.AsSpan(2)));
        // Exactly one: ping barrier on both replicas' ticks, then no second 0x009C for this actor.
        await TickAsync(replicaB, b.Session);
        await BarrierAsync(b.Stream);
        Assert.Equal(1, HistoryOf(b.Stream).Count(p => BinaryPrimitives.ReadInt16LittleEndian(p) == 0x009c && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2)) == 15u));
    }

    // A public-state (appearance) update published by replica A becomes visible in replica B's local
    // projection, a forged/stale presence identity cannot change it, and a replayed identical update
    // does not disturb it.
    [Fact]
    public async Task PublicStateChangeOnReplicaA_UpdatesReplicaBProjection_AndStalePresenceCannotOverwriteIt()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 17, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 18, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 17), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        var grain = _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var current = (await grain.PollPlayerFeedAsync(null, mapId)).Snapshot!.Single(e => e.Presence.ActorId == 17).PublicState;
        var changed = current with { HairStyle = 11, ClothesColor = 5 };
        var presenceId = a.Session.PresenceId!.Value;

        var forged = await replicaA.Runtime.UpdatePlayerPublicStateAsync(mapId, 17, Guid.NewGuid(), current with { HairStyle = 99 }, CancellationToken.None);
        Assert.Equal(WorldPlayerPublicStateUpdateStatus.StalePresence, forged.Status);
        var update = await replicaA.Runtime.UpdatePlayerPublicStateAsync(mapId, 17, presenceId, changed, CancellationToken.None);
        Assert.Equal(WorldPlayerPublicStateUpdateStatus.Updated, update.Status);

        await TickUntilAsync(replicaA, a.Session, replicaB, b.Session, () => replicaB.World.Players.TryGetByActorId(17, out var p) && p.HairStyle == 11);
        Assert.True(replicaB.World.Players.TryGetByActorId(17, out var projected));
        Assert.Equal((ushort)11, projected.HairStyle);
        Assert.Equal((ushort)5, projected.ClothesColor);

        // Replaying the same update (a caller retry) leaves the projection unchanged.
        await replicaA.Runtime.UpdatePlayerPublicStateAsync(mapId, 17, presenceId, changed, CancellationToken.None);
        await TickAsync(replicaB, b.Session);
        Assert.True(replicaB.World.Players.TryGetByActorId(17, out var afterReplay));
        Assert.Equal((ushort)11, afterReplay.HairStyle);
    }

    // A replica that starts (or restarts) AFTER players are already present bootstraps from World's
    // atomic snapshot: it discovers everyone exactly once, and everyone discovers it exactly once.
    [Fact]
    public async Task LateJoiningReplica_BootstrapsFromSnapshot_AndEveryoneDiscoversEachOtherExactlyOnce()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var replicaC = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 19, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 20, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 20), tick: TickBoth(replicaA, a.Session, replicaB, b.Session));

        var c = await ConnectAsync(replicaC, accountId: 21, mapId, 100, 101);
        using var _c = c.Client;
        Func<Task> tickAll = async () => { await TickAsync(replicaA, a.Session); await TickAsync(replicaB, b.Session); await TickAsync(replicaC, c.Session); };
        await ReadUntilAsync(c.Stream, packet => IsPlayerIntro(packet, 19), tick: tickAll);
        await ReadUntilAsync(c.Stream, packet => IsPlayerIntro(packet, 20), tick: tickAll);
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 21), tick: tickAll);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 21), tick: tickAll);

        for (var i = 0; i < 3; i++) await tickAll();
        foreach (var (stream, self) in new[] { (a.Stream, 19u), (b.Stream, 20u), (c.Stream, 21u) })
        {
            await BarrierAsync(stream);
            foreach (var other in new[] { 19u, 20u, 21u }.Where(id => id != self))
                Assert.Equal(1, HistoryOf(stream).Count(p => IsPlayerIntro(p, other)));
        }
    }

    // A client whose socket is already dead cannot stall, fault or corrupt delivery to the OTHER
    // observers on the same replica, and the replica's own tick keeps running.
    [Fact]
    public async Task DeadObserverSocket_DoesNotBlockOrCorruptOtherObserversOrTheTick()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 22, mapId, 99, 100);
        var dead = await ConnectAsync(replicaB, accountId: 23, mapId, 101, 100);
        var live = await ConnectAsync(replicaB, accountId: 24, mapId, 101, 101);
        using var _a = a.Client;
        using var _live = live.Client;
        Func<Task> tick = async () => { await TickAsync(replicaA, a.Session); await TickAsync(replicaB, dead.Session, live.Session); };
        await ReadUntilAsync(live.Stream, packet => IsPlayerIntro(packet, 22), tick: tick);

        // Abruptly kill the client side of one observer WITHOUT running the server's disconnect cleanup.
        dead.Client.Client.LingerState = new LingerOption(true, 0);
        dead.Client.Dispose();

        await a.Stream.WriteAsync(BuildMovementRequest(99, 102));
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyPlayerMove);
        var walk = await ReadUntilAsync(live.Stream, packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x09fd && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == 22u, tick: tick);
        Assert.Equal(22u, BinaryPrimitives.ReadUInt32LittleEndian(walk.AsSpan(5)));
        await tick(); // the tick itself still runs to completion with a dead observer registered.
    }

    // Map change on a populated destination. World appends TransferredIn inside the transfer RPC, so a
    // tick on the destination map's replica can observe it BEFORE the warping client has even been told
    // about the new map (0x0091) or finished loading it. Actor packets sent in that window are lost on
    // the client, and nothing would re-send them afterwards. Contract: nothing about other players is
    // written before the client reports the map loaded; right after, the warper is introduced to every
    // neighbour exactly once; and the neighbours (on either replica) see the warper exactly once.
    [Fact]
    public async Task MapChange_TickBetweenWorldTransferAndClientLoadEnd_NeitherSendsEarlyNorLosesNeighbours()
    {
        const string destination = "izlude";
        var replicaA = MakeReplica(destination);
        var replicaB = MakeReplica(destination);
        var stayer = await ConnectAsync(replicaA, accountId: 31, destination, 105, 100); // keeps replica A's destination tick alive.
        var remote = await ConnectAsync(replicaB, accountId: 32, destination, 101, 100);
        var warper = await ConnectAsync(replicaA, accountId: 30, "geffen", 100, 100);
        using var _s = stayer.Client;
        using var _r = remote.Client;
        using var _w = warper.Client;
        Func<Task> tickAll = async () =>
        {
            await TickAsync(replicaA, warper.Session, stayer.Session);
            await TickAsync(replicaB, remote.Session);
        };
        await ReadUntilAsync(stayer.Stream, packet => IsPlayerIntro(packet, 32), tick: tickAll);
        await ReadUntilAsync(remote.Stream, packet => IsPlayerIntro(packet, 31), tick: tickAll);

        await ((INpcScriptHost)warper.Session).WarpAsync(destination, 100, 101, CancellationToken.None);
        await ReadOpcodeAsync(warper.Stream, PacketConstants.ZcNpcAckMapMove);

        // Replica A's destination tick now processes TransferredIn(30) while the client is still "loading".
        for (var i = 0; i < 400 && !(replicaA.World.Players.TryGetByActorId(30, out var seen) && seen.MapName == destination); i++)
        {
            await tickAll();
            await Task.Delay(10);
        }
        Assert.True(replicaA.World.Players.TryGetByActorId(30, out var warperAtDestination), "TransferredIn was never applied.");
        Assert.Equal(destination, warperAtDestination.MapName);
        await tickAll();
        await BarrierAsync(warper.Stream);
        Assert.DoesNotContain(HistoryOf(warper.Stream), packet => IsPlayerEntry(packet, 31) || IsPlayerEntry(packet, 32));

        // The client finishes loading: now (and only now) it is introduced to its neighbours.
        await warper.Stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadUntilAsync(warper.Stream, packet => IsPlayerIntro(packet, 31) || IsPlayerEntry(packet, 31), tick: tickAll);
        await ReadUntilAsync(warper.Stream, packet => IsPlayerEntry(packet, 32), tick: tickAll);
        await ReadUntilAsync(remote.Stream, packet => IsPlayerEntry(packet, 30), tick: tickAll);
        for (var i = 0; i < 3; i++) await tickAll();
        await BarrierAsync(warper.Stream);
        await BarrierAsync(remote.Stream);
        await BarrierAsync(stayer.Stream);

        Assert.Equal(1, HistoryOf(warper.Stream).Count(packet => IsPlayerEntry(packet, 31)));
        Assert.Equal(1, HistoryOf(warper.Stream).Count(packet => IsPlayerEntry(packet, 32)));
        Assert.Equal(1, HistoryOf(remote.Stream).Count(packet => IsPlayerEntry(packet, 30)));
        Assert.Equal(1, HistoryOf(stayer.Stream).Count(packet => IsPlayerEntry(packet, 30)));
    }

    // ---- Live-client regression: a continuously walking player must reach an observer as ONE walk. ----
    // World emits a feed entry for every authoritative crossed cell (needed for position/AOI authority),
    // but the stock client animates a 0x09FD walk entry on its own along the whole route; re-sending it
    // for each ordinary cell restarts/stutters that animation. Only a walk START (and a genuine new
    // route) may produce a walking packet; crossed cells and completion update the registry/AOI only.

    private static (int StartX, int StartY, int DestX, int DestY) DecodeWalk(byte[] packet)
    {
        var b = packet.AsSpan(67, 6);
        return ((b[0] << 2) | (b[1] >> 6), ((b[1] & 0x3f) << 4) | (b[2] >> 4), ((b[2] & 0x0f) << 6) | (b[3] >> 2), ((b[3] & 0x03) << 8) | b[4]);
    }

    private static bool IsWalkEntry(byte[] packet, uint actorId) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x09fd && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == actorId;

    private static List<byte[]> WalkEntries(Stream stream, uint actorId) => HistoryOf(stream).Where(p => IsWalkEntry(p, actorId)).ToList();

    private async Task<int> CommittedPlayerEntriesAsync(string mapId, uint actorId, WorldPlayerFeedEntryKind kind)
    {
        var grain = _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var baseline = await grain.PollPlayerFeedAsync(null, mapId);
        var page = await grain.PollPlayerFeedAsync(new WorldPlayerFeedCursor(baseline.Epoch, 0), mapId);
        Assert.NotNull(page.Entries);
        return page.Entries!.Count(e => e.Kind == kind && e.ActorId == actorId);
    }

    // Ticks both replicas until World reports the player idle at (x,y), recording every distinct cell the
    // OBSERVER replica's local registry held for the player on the way (proof that intermediate
    // authoritative cells still reach the observer's registry/AOI, just not as client packets).
    private async Task<HashSet<(ushort, ushort)>> TickWalkToIdleAsync(Replica observerReplica, string mapId, uint actorId, ushort x, ushort y, Func<Task> tick)
    {
        var seen = new HashSet<(ushort, ushort)>();
        var grain = _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await tick();
            if (observerReplica.World.Players.TryGetByActorId(actorId, out var seenPresence)) seen.Add((seenPresence.X, seenPresence.Y));
            var entry = (await grain.PollPlayerFeedAsync(null, mapId)).Snapshot!.Single(e => e.Presence.ActorId == actorId);
            if (entry.Movement is null && entry.Presence.X == x && entry.Presence.Y == y)
            {
                await tick();
                await tick();
                return seen;
            }
            await Task.Delay(15);
        }
        throw new TimeoutException($"Actor {actorId} never became idle at ({x},{y}).");
    }

    [Fact]
    public async Task UninterruptedWalk_ProducesExactlyOneWalkingStartPacketToAnAlreadyVisibleObserver_WhileEveryCellStillUpdatesRegistryAndAoi()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 50, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 51, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        var tick = TickBoth(replicaA, a.Session, replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 50), tick: tick);

        await a.Stream.WriteAsync(BuildMovementRequest(99, 106));
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyPlayerMove);
        var cellsSeenByObserver = await TickWalkToIdleAsync(replicaB, mapId, 50, 99, 106, tick);
        await BarrierAsync(b.Stream);

        // World still authoritatively emitted every crossed cell...
        Assert.True(await CommittedPlayerEntriesAsync(mapId, 50, WorldPlayerFeedEntryKind.Moved) >= 4);
        // ...the observer's registry/AOI followed along (several distinct intermediate cells, final cell, walk ended)...
        Assert.True(cellsSeenByObserver.Count >= 3, $"observer registry only ever held {cellsSeenByObserver.Count} cell(s)");
        Assert.True(replicaB.World.Players.TryGetByActorId(50, out var finalPresence));
        Assert.Equal(((ushort)99, (ushort)106), (finalPresence.X, finalPresence.Y));
        Assert.Null(finalPresence.Movement);
        // ...but the observer's CLIENT got exactly one walking packet for the whole walk, describing the whole route.
        var walks = WalkEntries(b.Stream, 50);
        var only = Assert.Single(walks);
        Assert.Equal((99, 100, 99, 106), DecodeWalk(only));
        // Completion does not restart it, and nothing further arrives.
        await tick();
        await BarrierAsync(b.Stream);
        Assert.Single(WalkEntries(b.Stream, 50));
    }

    [Fact]
    public async Task PlayerEnteringAoiMidWalk_IsIntroducedOnceAtItsCurrentCell_WithAWalkingStateAndThisGatewaysOwnTick()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 52, mapId, 101, 120);   // 20 cells away: outside AOI (14).
        var b = await ConnectAsync(replicaB, accountId: 53, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        var tick = TickBoth(replicaA, a.Session, replicaB, b.Session);
        await tick();

        await a.Stream.WriteAsync(BuildMovementRequest(101, 108));
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyPlayerMove);
        var localTickBefore = unchecked((uint)Environment.TickCount);
        await TickWalkToIdleAsync(replicaB, mapId, 52, 101, 108, tick);
        await BarrierAsync(b.Stream);

        var entry = Assert.Single(HistoryOf(b.Stream), packet => IsPlayerEntry(packet, 52));
        Assert.True(IsWalkEntry(entry, 52), "entering mid-walk must be introduced with a walking state, not a stand/spawn entry");
        var (startX, startY, destX, destY) = DecodeWalk(entry);
        Assert.Equal((101, 108), (destX, destY));
        Assert.Equal(101, startX);
        Assert.InRange(startY, 108, 119);   // the CURRENT cell on the route when it came into view, never the walk's original start (120).
        // Stamped with THIS gateway's clock at send time (pinned clif_set_unit_walking: client_tick(gettick())).
        var stamp = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(37));
        Assert.True(unchecked(stamp - localTickBefore) < 60_000, $"walk start tick {stamp} is not this gateway's tick ({localTickBefore})");
        Assert.Empty(HistoryOf(b.Stream).Where(packet => IsPlayerVanish(packet, 52)));
    }

    [Fact]
    public async Task PlayerLeavingAoiMidWalk_DisappearsExactlyOnce_AndNoWalkPacketFollowsTheVanish()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 54, mapId, 101, 110);
        var b = await ConnectAsync(replicaB, accountId: 55, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        var tick = TickBoth(replicaA, a.Session, replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 54), tick: tick);

        await a.Stream.WriteAsync(BuildMovementRequest(101, 122));
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyPlayerMove);
        await TickWalkToIdleAsync(replicaB, mapId, 54, 101, 122, tick);
        await BarrierAsync(b.Stream);

        var history = HistoryOf(b.Stream).ToList();
        var vanishIndex = history.FindIndex(packet => IsPlayerVanish(packet, 54));
        Assert.True(vanishIndex >= 0, "the walker never left the observer's view");
        Assert.Equal(1, history.Count(packet => IsPlayerVanish(packet, 54)));
        Assert.Empty(history.Skip(vanishIndex + 1).Where(packet => IsPlayerEntry(packet, 54)));
        Assert.Single(WalkEntries(b.Stream, 54));   // the single walk start, sent while still in view.
    }

    [Fact]
    public async Task MidWalkRetarget_ProjectsTheNewRouteOnce_WithoutStaleOrDuplicateWalks()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var a = await ConnectAsync(replicaA, accountId: 56, mapId, 99, 100);
        var b = await ConnectAsync(replicaB, accountId: 57, mapId, 101, 100);
        using var _a = a.Client;
        using var _b = b.Client;
        var tick = TickBoth(replicaA, a.Session, replicaB, b.Session);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 56), tick: tick);
        var grain = _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));

        await a.Stream.WriteAsync(BuildMovementRequest(99, 112));
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyPlayerMove);
        // Let the walker get a few cells along before re-clicking.
        for (var i = 0; i < 400; i++)
        {
            await tick();
            var snap = (await grain.PollPlayerFeedAsync(null, mapId)).Snapshot!.Single(e => e.Presence.ActorId == 56);
            if (snap.Presence.Y >= 103) break;
            await Task.Delay(15);
        }
        await a.Stream.WriteAsync(BuildMovementRequest(105, 104));
        await TickWalkToIdleAsync(replicaB, mapId, 56, 105, 104, tick);
        await BarrierAsync(b.Stream);

        var walks = WalkEntries(b.Stream, 56).Select(DecodeWalk).ToList();
        Assert.Equal(2, walks.Count);                                   // the original start + the genuine new route, nothing else.
        Assert.Equal((99, 100, 99, 112), walks[0]);
        Assert.Equal(105, walks[1].DestX);
        Assert.Equal(104, walks[1].DestY);
        Assert.InRange(walks[1].StartY, 103, 112);                      // starts from where the walker really was, not the stale origin.
        Assert.True(replicaB.World.Players.TryGetByActorId(56, out var final));
        Assert.Equal(((ushort)105, (ushort)104), (final.X, final.Y));
        Assert.Null(final.Movement);
    }

    // The tick in a walk entry must be the SENDING gateway's clock: replicas on different machines have
    // unrelated Environment.TickCount values, and the client interpolates against the tick base it learned
    // from ITS OWN gateway. A presence carrying another gateway's stamp must not leak it onto the wire.
    [Fact]
    public async Task WalkEntry_NeverForwardsAnotherGatewaysTickStamp()
    {
        const string mapId = "izlude";
        var replica = MakeReplica(mapId);
        var observer = await ConnectAsync(replica, accountId: 58, mapId, 100, 100);
        using var _o = observer.Client;
        var foreignStamp = unchecked((uint)Environment.TickCount + 1_000_000_007u);
        var walker = new PlayerPresence(59, 59, "Walker", mapId, 101, 101, 0, 0, new PlayerMovementPresence(101, 101, 101, 105, foreignStamp),
            0, 0, 50, 150, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        await observer.Session.PlayerEnteredViewAsync(walker, PlayerEntryKind.ExistingStandingOrWalking, CancellationToken.None);
        var entry = await ReadOpcodeAsync(observer.Stream, 0x09fd);

        var stamp = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(37));
        Assert.NotEqual(foreignStamp, stamp);
        Assert.True(unchecked((uint)Environment.TickCount - stamp) < 10_000);
    }

    // Point 9-11: a player -> monster combat action originating on replica A reaches an observer on
    // replica B exactly once, and neither replica ever produces a same-gateway duplicate from feed
    // replay (the attacker's own fast-path echo is deduplicated against its own later feed delivery
    // - see MapClientSession.ProjectPlayerAttackActionAsync's own doc comment).
    [Fact]
    public async Task PlayerAttackOnReplicaA_ReachesObserverOnReplicaB_ExactlyOnce_NoSameGatewayDuplicate()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        // Weak attacker so Poring's 55 HP survives a single non-lethal hit.
        var weakAttacker = new CharacterGameplayState(11, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 9, 9, 9, 9, 9, 9);
        var a = await ConnectAsync(replicaA, accountId: 11, mapId, (ushort)(MonsterX - 1), MonsterY, weakAttacker);
        var b = await ConnectAsync(replicaB, accountId: 12, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _a = a.Client;
        using var _b = b.Client;

        // Bootstrap both replicas' monster projections against the SAME World-authoritative spawn.
        await TickAsync(replicaA, a.Session); // SpawnInitializationRequired -> load.
        await TickAsync(replicaA, a.Session); // Bootstrap.
        await TickAsync(replicaB, b.Session); // Bootstrap (shared World state, already loaded by replica A above).
        var introA = await ReadUntilAsync(a.Stream, IsAnyMonsterIntro);
        var monsterActorId = BinaryPrimitives.ReadUInt32LittleEndian(introA.AsSpan(5));
        await ReadUntilAsync(b.Stream, packet => IsMonsterIntro(packet, monsterActorId));

        // Mutual player discovery. Unmatched packets stay queued, so a player intro that arrived
        // before the monster intro above is still available here.
        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 12), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 11), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());

        await a.Stream.WriteAsync(BuildAttackPacket(monsterActorId));
        // A's own local fast-path echo of its own action.
        await ReadOpcodeAsync(a.Stream, PacketConstants.ZcStopMove); // 0x0088 fixpos.
        var attackerAction = await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyAct3);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(attackerAction));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(attackerAction.AsSpan(2)));

        // B's own replica poll observes the SAME action via the World feed - exactly once.
        await TickAsync(replicaB, b.Session);
        var observerAction = await ReadOpcodeAsync(b.Stream, PacketConstants.ZcNotifyAct3);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(observerAction));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(observerAction.AsSpan(2)));
        Assert.Equal(monsterActorId, BinaryPrimitives.ReadUInt32LittleEndian(observerAction.AsSpan(6)));

        // Exactly-once: a single attack request also starts the server's repeat-attack loop, so more
        // than one hit may legitimately land. The invariant is therefore compared against the
        // authoritative World feed: every PlayerAttackAction entry World committed must have reached
        // EACH socket exactly once (attacker echo + its own later feed delivery deduplicated; the
        // observer on the other replica receives it only via the feed). A duplicate or a loss makes
        // the per-socket count differ from World's count. Other 0x08C8 traffic (the Poring attacking
        // a player) is a different action and is excluded by attacker id.
        const uint attackerId = 11;
        for (var attempt = 0; ; attempt++)
        {
            await TickAsync(replicaA, a.Session);
            await TickAsync(replicaB, b.Session);
            await BarrierAsync(a.Stream);
            await BarrierAsync(b.Stream);
            var committedBefore = await CommittedPlayerAttackCountAsync(mapId, attackerId);
            var onA = PlayerAttackActionsSeen(a.Stream, attackerId);
            var onB = PlayerAttackActionsSeen(b.Stream, attackerId);
            var committedAfter = await CommittedPlayerAttackCountAsync(mapId, attackerId);
            if (committedBefore == committedAfter && onA == committedAfter && onB == committedAfter && committedAfter >= 1) break;
            Assert.True(attempt < 20 && onA <= committedAfter && onB <= committedAfter,
                $"player-attack delivery mismatch: world={committedAfter} replicaA-socket={onA} replicaB-socket={onB}");
            await Task.Delay(50);
        }
    }

    private async Task<int> CommittedPlayerAttackCountAsync(string mapId, uint attackerId)
    {
        var grain = _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        var baseline = await grain.PollMonsterFeedAsync(null, mapId);
        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(baseline.SimulationEpoch, 0), mapId);
        Assert.NotNull(page.Entries);
        return page.Entries!.Count(e => e.Kind == WorldMonsterFeedEntryKind.PlayerAttackAction && e.PlayerAttack?.AttackerActorId == attackerId);
    }

    // 0x08C8 whose source is the given PLAYER (offset 2) - history plus anything still queued.
    private static int PlayerAttackActionsSeen(Stream stream, uint attackerId) =>
        HistoryOf(stream).Count(p => BinaryPrimitives.ReadInt16LittleEndian(p) == (short)PacketConstants.ZcNotifyAct3 && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2)) == attackerId);

    // Ping round-trip: every packet the session queued before the ping has reached the socket.
    private static async Task BarrierAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        await ReadOpcodeAsync(stream, PacketConstants.ZcPingLive);
    }

    // Monster -> player combat: the monster's attack on a player of replica A is resolved and written to
    // that player's socket locally, published to World, and projected to the observer on replica B. The
    // exactly-once invariant mirrors the player-attack one: per socket, the number of monster->victim
    // 0x08C8 equals the number of MonsterAttackAction entries World committed - so neither the victim's
    // own replica (which also reads the entry back from the feed) nor the remote replica may duplicate.
    [Fact]
    public async Task MonsterAttackOnReplicaAPlayer_ReachesReplicaBObserverExactlyOnce_AndIsNotDuplicatedOnTheVictimsReplica()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var weakAttacker = new CharacterGameplayState(41, 1, 0, 1, 1, 0, 0, 40, 10, 40, 10, 0, 0, 9, 9, 9, 9, 9, 9);
        var a = await ConnectAsync(replicaA, accountId: 41, mapId, (ushort)(MonsterX - 1), MonsterY, weakAttacker);
        var b = await ConnectAsync(replicaB, accountId: 42, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _a = a.Client;
        using var _b = b.Client;
        Func<Task> tickBoth = TickBoth(replicaA, a.Session, replicaB, b.Session);
        var monsterIntro = await ReadUntilAsync(a.Stream, IsAnyMonsterIntro, tick: tickBoth);
        var monsterActorId = BinaryPrimitives.ReadUInt32LittleEndian(monsterIntro.AsSpan(5));
        await ReadUntilAsync(b.Stream, packet => IsMonsterIntro(packet, monsterActorId), tick: tickBoth);

        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 42), tick: tickBoth);
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 41), tick: tickBoth);

        // Engage the Poring so it retaliates against player 41.
        await a.Stream.WriteAsync(BuildAttackPacket(monsterActorId));
        var grain = _cluster.Client.GetGrain<IWorldPartitionGrain>(Resolver().ResolvePartition(mapId));
        async Task<int> CommittedMonsterAttacksAsync()
        {
            var baseline = await grain.PollMonsterFeedAsync(null, mapId);
            var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(baseline.SimulationEpoch, 0), mapId);
            Assert.NotNull(page.Entries);
            return page.Entries!.Count(e => e.Kind == WorldMonsterFeedEntryKind.MonsterAttackAction && e.ActorId == monsterActorId && e.MonsterAttack!.TargetActorId == 41);
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (await CommittedMonsterAttacksAsync() < 1 && DateTime.UtcNow < deadline)
        {
            await tickBoth();
            await Task.Delay(50);
        }
        if (await CommittedMonsterAttacksAsync() < 1)
        {
            var baseline = await grain.PollMonsterFeedAsync(null, mapId);
            var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(baseline.SimulationEpoch, 0), mapId);
            Assert.Fail("The monster never retaliated against the player. World feed: " + string.Join(' ', page.Entries!.Select(e => $"{e.Kind}@{e.Sequence}/actor={e.ActorId}/target={e.MonsterAttack?.TargetActorId}")) + $" (monster={monsterActorId})");
        }

        int MonsterHits(Stream stream) => HistoryOf(stream).Count(p =>
            BinaryPrimitives.ReadInt16LittleEndian(p) == (short)PacketConstants.ZcNotifyAct3 &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(2)) == monsterActorId &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(6)) == 41u);
        for (var attempt = 0; ; attempt++)
        {
            await tickBoth();
            await BarrierAsync(a.Stream);
            await BarrierAsync(b.Stream);
            var before = await CommittedMonsterAttacksAsync();
            var onVictim = MonsterHits(a.Stream);
            var onObserver = MonsterHits(b.Stream);
            var after = await CommittedMonsterAttacksAsync();
            if (before == after && onVictim == after && onObserver == after) break;
            Assert.True(attempt < 20 && onVictim <= after && onObserver <= after,
                $"monster-attack delivery mismatch: world={after} victim-socket={onVictim} observer-socket={onObserver}");
            await Task.Delay(50);
        }
    }

    // Point 10: for a lethal player hit, the observer on the OTHER replica observes the killing
    // combat action before the monster's Died vanish - the structural feed-ordering guarantee (see
    // WorldMonsterMapSimulation.ApplyDamage's own doc comment), proven here across two genuinely
    // independent gateway processes, not merely within one process's own LethalAttackProjectionGate.
    [Fact]
    public async Task LethalPlayerHitOnReplicaA_ObserverOnReplicaB_SeesActionBeforeDiedVanish()
    {
        const string mapId = "geffen";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);

        var a = await ConnectAsync(replicaA, accountId: 13, mapId, (ushort)(MonsterX - 1), MonsterY);
        var b = await ConnectAsync(replicaB, accountId: 14, mapId, (ushort)(MonsterX + 1), MonsterY);
        using var _a = a.Client;
        using var _b = b.Client;

        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaA, a.Session);
        await TickAsync(replicaB, b.Session);
        var introA = await ReadUntilAsync(a.Stream, IsAnyMonsterIntro);
        var monsterActorId = BinaryPrimitives.ReadUInt32LittleEndian(introA.AsSpan(5));
        await ReadUntilAsync(b.Stream, packet => IsMonsterIntro(packet, monsterActorId));

        await ReadUntilAsync(a.Stream, packet => IsPlayerIntro(packet, 14), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());
        await ReadUntilAsync(b.Stream, packet => IsPlayerIntro(packet, 13), tick: () => TickAsync(replicaA, a.Session).ContinueWith(_ => TickAsync(replicaB, b.Session)).Unwrap());

        // Repeated attacks until the kill lands (default full-strength attacker vs. Poring's 55 HP).
        var killed = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        byte[]? observerAction = null;
        byte[]? observerVanish = null;
        while (!killed && DateTime.UtcNow < deadline)
        {
            await a.Stream.WriteAsync(BuildAttackPacket(monsterActorId));
            await ReadOpcodeAsync(a.Stream, PacketConstants.ZcStopMove);
            var action = await ReadOpcodeAsync(a.Stream, PacketConstants.ZcNotifyAct3);
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(22));
            // Peek whether this hit's own local HP-info write reports 0 (lethal) without consuming
            // further unrelated bytes - HP-info always immediately follows the action for the
            // attacker's own session.
            var hpInfo = await ReadOpcodeAsync(a.Stream, PacketConstants.ZcHpInfo);
            var hpAfter = BinaryPrimitives.ReadUInt32LittleEndian(hpInfo.AsSpan(6));
            if (hpAfter == 0)
            {
                killed = true;
                await TickAsync(replicaB, b.Session);
                observerAction = await ReadOpcodeAsync(b.Stream, PacketConstants.ZcNotifyAct3);
                observerVanish = await ReadUntilAsync(b.Stream, packet => IsMonsterDiedVanish(packet, monsterActorId));
            }
            else
            {
                await TickAsync(replicaA, a.Session);
                await TickAsync(replicaB, b.Session);
                // Drain the observer's own copy of this non-lethal action before the next attack.
                await ReadOpcodeAsync(b.Stream, PacketConstants.ZcNotifyAct3);
            }
        }
        Assert.True(killed, "Expected the attacker to eventually land the killing blow.");
        Assert.NotNull(observerAction);
        Assert.NotNull(observerVanish);
        Assert.Equal((short)PacketConstants.ZcNotifyAct3, BinaryPrimitives.ReadInt16LittleEndian(observerAction));
        Assert.Equal((short)PacketConstants.ZcNotifyVanish, BinaryPrimitives.ReadInt16LittleEndian(observerVanish));
        Assert.Equal(PacketConstants.ZcNotifyVanishReasonDied, observerVanish![6]);
        // Ordering is asserted against the observer socket's own arrival-ordered history, not
        // against read order: the lethal action must precede the Died vanish on the wire.
        var history = HistoryOf(b.Stream);
        var vanishIndex = history.ToList().FindIndex(p => ReferenceEquals(p, observerVanish));
        var actionIndex = history.ToList().FindIndex(p => ReferenceEquals(p, observerAction));
        Assert.True(actionIndex >= 0 && vanishIndex >= 0 && actionIndex < vanishIndex, "Lethal action must be delivered before the Died vanish.");
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 200) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(Resolver())
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([MakeAllWalkableMap("izlude"), MakeAllWalkableMap("geffen")]))
                .AddSingleton<TimeProvider>(LeaseClock);
        }
    }
}
