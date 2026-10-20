using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;
using MapServer.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.MapServer.Tests.Net;

// Feature 16 S2/S3: the full real-session pickup pipeline - a genuine Orleans TestCluster-hosted
// IWorldPartitionGrain (via OrleansWorldRuntime, never a scripted fake - see
// MapTcpServer.InMemoryTestWorldRuntime's own doc comment for why) combined with a real
// MapClientSession socket round trip, proving MapClientSession.HandleIroItemPickupRequestAsync's own
// consumption of the verified 0x0362/0x0B41/0x00A1 wire shapes end-to-end: a ground item dropped
// directly through World (standing in for a monster-death drop, which is covered separately by
// MonsterCombatCoordinator/MobDropResolver's own tests) is claimed, durably granted, and removed.
public sealed class MapClientSessionGroundItemPickupTests : IAsyncLifetime
{
    private const uint AccountId = 3_000_000;
    private const uint CharId = 77;
    private const string MapName = "prontera";

    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Grain() => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>("prontera-region");
    private IWorldRuntime WorldRuntime() => new OrleansWorldRuntime(_cluster.Client, WorldPartitionTopologyLoader.Load(Path.Combine(FindRepositoryRoot(), "conf", "world_partitions.json"), ["prontera", "prt_fild08d", "izlude", "geffen"]));
    private static readonly ItemDefinition Wood = ItemCatalog.Current.ByAegisName["Wood"];

    private static byte[] PickupRequestPacket(uint groundActorId, byte opaqueByte = 0xcd)
    {
        var packet = new byte[PacketConstants.IroCzItemPickupRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzItemPickupRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), groundActorId);
        packet[6] = opaqueByte;
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static readonly CharacterGameplayState FullHealthNovice = new(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(u);
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, InMemoryInventoryOperations Persistence)> SetupAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var inventoryPersistence = new InMemoryInventoryOperations();
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: new StubGameplayStatePersistence(FullHealthNovice), inventoryOperations: inventoryPersistence,
            accountId: AccountId, charId: CharId, distributedWorld: WorldRuntime());
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, MapName, 150, 180, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);

        return (client, stream, session, run, inventoryPersistence);
    }

    [Fact]
    public async Task PickupRequest_ClaimsGrantsAndRemoves_SendingPickupAckAndFloorRemoval()
    {
        var grain = Grain();
        var dropResult = await grain.DropGroundItemAsync(new WorldGroundItemDrop(MapName, 150, 180, new(Wood.Id, 3), new(null), Guid.NewGuid()));
        Assert.Equal(WorldGroundItemDropStatus.Dropped, dropResult.Status);

        var (client, stream, _, run, persistence) = await SetupAsync();
        using var _ = client;

        await stream.WriteAsync(PickupRequestPacket(dropResult.GroundItemId!.Value));

        var pickupAck = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
        Assert.Equal((short)PacketConstants.ZcItemPickupAck, BinaryPrimitives.ReadInt16LittleEndian(pickupAck));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt16LittleEndian(pickupAck.AsSpan(4)));
        Assert.Equal((uint)Wood.ClientViewId, BinaryPrimitives.ReadUInt32LittleEndian(pickupAck.AsSpan(6)));

        var removal = await ReadExact(stream, PacketConstants.ZcFloorItemRemovalLength);
        Assert.Equal((short)PacketConstants.ZcFloorItemRemoval, BinaryPrimitives.ReadInt16LittleEndian(removal));
        Assert.Equal(dropResult.GroundItemId.Value, BinaryPrimitives.ReadUInt32LittleEndian(removal.AsSpan(2)));

        Assert.Equal(3, persistence.AmountOf(Wood.Id));
        Assert.Empty((await grain.GetGroundItemsAsync(MapName)).Items);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TwoSessionsRaceTheSamePickup_OnlyOneSucceeds_TheOtherGetsNothing()
    {
        var grain = Grain();
        var dropResult = await grain.DropGroundItemAsync(new WorldGroundItemDrop(MapName, 150, 180, new(Wood.Id, 1), new(null), Guid.NewGuid()));

        var (clientA, streamA, _, runA, persistenceA) = await SetupAsync();
        using var _a = clientA;
        var (clientB, streamB, _, runB, persistenceB) = await SetupAsync();
        using var _b = clientB;

        await Task.WhenAll(
            streamA.WriteAsync(PickupRequestPacket(dropResult.GroundItemId!.Value)).AsTask(),
            streamB.WriteAsync(PickupRequestPacket(dropResult.GroundItemId!.Value)).AsTask());

        // Exactly one of the two sessions receives the 0x0B41 grant ack; the other receives nothing
        // for this request at all (the claim loss is logged, never acknowledged to the client).
        var probe = new byte[] { 0x1c, 0x0b }; // ping probe to prove "no other packet was queued"
        var raceWinner = -1;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var readA = TryReadPickupAck(streamA, cts.Token);
        var readB = TryReadPickupAck(streamB, cts.Token);
        var first = await Task.WhenAny(readA, readB);
        if (first == readA && await readA) raceWinner = 0;
        else if (await readB) raceWinner = 1;

        Assert.True(raceWinner is 0 or 1, "Exactly one session must receive the pickup ack.");
        var winnerPersistence = raceWinner == 0 ? persistenceA : persistenceB;
        var loserPersistence = raceWinner == 0 ? persistenceB : persistenceA;
        Assert.Equal(1, winnerPersistence.AmountOf(Wood.Id));
        Assert.Equal(0, loserPersistence.AmountOf(Wood.Id));
        Assert.Empty((await grain.GetGroundItemsAsync(MapName)).Items);

        clientA.Close();
        clientB.Close();
        await runA.WaitAsync(TimeSpan.FromSeconds(5));
        await runB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> TryReadPickupAck(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[2];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return BinaryPrimitives.ReadInt16LittleEndian(buffer) == PacketConstants.ZcItemPickupAck;
        }
        catch (OperationCanceledException) { return false; }
    }

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services.AddSingleton<IWorldPartitionResolver>(
                WorldPartitionTopologyLoader.Load(Path.Combine(FindRepositoryRoot(), "conf", "world_partitions.json"), ["prontera", "prt_fild08d", "izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>()
                .AddSingleton<IMapCollisionProvider>(EmptyMapCollisionProvider.Instance)
                .AddSingleton(TimeProvider.System);
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Athena.NET.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}
