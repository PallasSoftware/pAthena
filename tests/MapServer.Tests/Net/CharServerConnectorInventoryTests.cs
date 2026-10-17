using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Tests.Net;

/// <summary>
/// The real CharServerConnector against a fake CharServer socket: the inventory operation and container-read exchanges, including the
/// cases where no answer ever arrives (the outcome must then be reported as UNKNOWN, never as a failure).
/// </summary>
public sealed class CharServerConnectorInventoryTests : IAsyncDisposable
{
    private static readonly string Token = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private Task? _run;
    private TcpClient? _server;

    private async Task<(CharServerConnector Connector, NetworkStream Server)> ConnectAsync(TimeSpan? operationTimeout = null)
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var configStore = new MapConfigStore(new MapConfig { CharIp = IPAddress.Loopback, CharPort = port, ServiceId = "TestMapServer", MapIp = IPAddress.Loopback, MapPort = 5121 }, "unused.conf");
        var connector = new CharServerConnector(configStore, new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = Token }))
        {
            InventoryOperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(5),
        };
        _run = connector.RunAsync(_cts.Token);
        _server = await _listener.AcceptTcpClientAsync(_cts.Token);
        var stream = _server.GetStream();

        await ReadExactAsync(stream, 2 + 24 + 4 + 2 + 2);                                         // MapServiceHello
        var challenge = new byte[2 + 32];
        BinaryPrimitives.WriteInt16LittleEndian(challenge, PacketConstants.MapServiceAuthChallenge);
        await stream.WriteAsync(challenge, _cts.Token);
        await ReadExactAsync(stream, 2 + 32);                                                      // MapServiceAuthProof (not verified here)
        var result = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(result, PacketConstants.MapServiceAuthResult);
        await stream.WriteAsync(result, _cts.Token);
        await connector.WaitUntilReadyAsync(_cts.Token);
        // MapServer announces its map list and user count right after authenticating; skip whatever it sends before the request under test.
        return (connector, stream);
    }

    private async Task<byte[]> ReadPacketOfAsync(NetworkStream stream, short opcode)
    {
        while (true)
        {
            var header = await ReadExactAsync(stream, 2);
            var type = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (type == opcode)
            {
                if (opcode == InventoryOperationWire.RequestOpcode)
                {
                    var length = await ReadExactAsync(stream, 2);
                    var rest = await ReadExactAsync(stream, BinaryPrimitives.ReadUInt16LittleEndian(length) - 4);
                    return [.. header, .. length, .. rest];
                }
                return [.. header, .. await ReadExactAsync(stream, InventoryListWire.RequestLength - 2)];
            }
            // Unrelated startup traffic (map list = variable, user count = 6 bytes): drain it.
            if (type == PacketConstants.MapSendUserCount) await ReadExactAsync(stream, 4);
            else
            {
                var length = await ReadExactAsync(stream, 2);
                await ReadExactAsync(stream, BinaryPrimitives.ReadUInt16LittleEndian(length) - 4);
            }
        }
    }

    private async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, _cts.Token);
        return buffer;
    }

    [Fact]
    public async Task Execute_SendsTheOperation_AndReturnsCharServersAnswer()
    {
        var (connector, server) = await ConnectAsync();
        var request = new InventoryOperationRequest(7, 9, Guid.NewGuid(), InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 3))]);

        var pending = connector.ExecuteAsync(request, _cts.Token);
        var received = await ReadPacketOfAsync(server, InventoryOperationWire.RequestOpcode);
        Assert.True(InventoryOperationWire.TryParseRequest(received, out var parsed));
        Assert.Equal(request.OperationId, parsed.OperationId);
        Assert.Equal(request.Mutations, parsed.Mutations);

        var row = InventoryItemRow.New(501, 3) with { DurableId = 11, Revision = 1 };
        await server.WriteAsync(InventoryOperationWire.BuildResponse(new(InventoryOperationStatus.Applied, 9, request.OperationId, -1, [new(11, false, row)])), _cts.Token);

        var response = await pending;
        Assert.NotNull(response);
        Assert.Equal(InventoryOperationStatus.Applied, response!.Status);
        Assert.Equal(row, response.Results.Single().Row);
    }

    [Fact]
    public async Task Execute_WithoutAnAnswer_ReportsUnknown_NotFailure()
    {
        var (connector, server) = await ConnectAsync(TimeSpan.FromMilliseconds(300));
        var request = new InventoryOperationRequest(7, 9, Guid.NewGuid(), InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 3))]);

        var response = await connector.ExecuteAsync(request, _cts.Token);   // the fake CharServer reads it but never answers

        Assert.Null(response);
        // The same OperationId can be sent again afterwards (the pending entry was cleaned up).
        var again = connector.ExecuteAsync(request, _cts.Token);
        await ReadPacketOfAsync(server, InventoryOperationWire.RequestOpcode);
        await ReadPacketOfAsync(server, InventoryOperationWire.RequestOpcode);
        Assert.Null(await again);
    }

    [Fact]
    public async Task Execute_WhenTheConnectionDrops_ReportsUnknown()
    {
        var (connector, server) = await ConnectAsync();
        var request = new InventoryOperationRequest(7, 9, Guid.NewGuid(), InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 3))]);

        var pending = connector.ExecuteAsync(request, _cts.Token);
        await ReadPacketOfAsync(server, InventoryOperationWire.RequestOpcode);
        _server!.Close();

        Assert.Null(await pending);
    }

    [Fact]
    public async Task Execute_IgnoresAnswersForOtherOperations()
    {
        var (connector, server) = await ConnectAsync(TimeSpan.FromMilliseconds(400));
        var request = new InventoryOperationRequest(7, 9, Guid.NewGuid(), InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 3))]);

        var pending = connector.ExecuteAsync(request, _cts.Token);
        await ReadPacketOfAsync(server, InventoryOperationWire.RequestOpcode);
        await server.WriteAsync(InventoryOperationWire.BuildResponse(new(InventoryOperationStatus.Applied, 9, Guid.NewGuid(), -1, [])), _cts.Token);

        Assert.Null(await pending);
    }

    [Fact]
    public async Task GetContainer_ReturnsTheRequestedContainersRows()
    {
        var (connector, server) = await ConnectAsync();

        var pending = connector.GetContainerAsync(7, 9, InventoryContainer.Storage, _cts.Token);
        var request = await ReadPacketOfAsync(server, InventoryListWire.RequestOpcode);
        Assert.True(InventoryListWire.TryParseGet(request, out var account, out var character, out var container));
        Assert.Equal((7u, 9u, InventoryContainer.Storage), (account, character, container));

        var rows = new[] { InventoryItemRow.New(501, 3) with { DurableId = 4, Revision = 2 } };
        await server.WriteAsync(InventoryListWire.BuildResponse(0, 9, InventoryContainer.Storage, rows), _cts.Token);

        var (succeeded, read) = await pending;
        Assert.True(succeeded);
        Assert.Equal(rows, read);
    }

    [Fact]
    public async Task GetInventory_BuildsTheSnapshotWithDenseSlotsAndRevisions()
    {
        var (connector, server) = await ConnectAsync();

        var pending = connector.GetInventoryAsync(7, 9, _cts.Token);
        await ReadPacketOfAsync(server, InventoryListWire.RequestOpcode);
        await server.WriteAsync(InventoryListWire.BuildResponse(0, 9, InventoryContainer.Inventory,
            [InventoryItemRow.New(1201, 1) with { DurableId = 3, Equip = 2, Revision = 5 }, InventoryItemRow.New(501, 2) with { DurableId = 8, Revision = 1 }]), _cts.Token);

        var read = await pending;
        Assert.True(read.Succeeded);
        Assert.Equal([(3u, 0u, 1201, 5u), (8u, 1u, 501, 1u)], read.Snapshot!.Items.Select(i => (i.DurableId, i.SlotIndex, i.ItemId, i.Details.Revision)));
    }

    [Fact]
    public async Task NotConnected_ReportsUnknownAndFailedReads()
    {
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var request = new InventoryOperationRequest(7, 9, Guid.NewGuid(), InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 3))]);

        Assert.Null(await connector.ExecuteAsync(request, _cts.Token));
        Assert.False((await connector.GetInventoryAsync(7, 9, _cts.Token)).Succeeded);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _server?.Dispose();
        _listener.Stop();
        if (_run is not null) { try { await _run; } catch (OperationCanceledException) { } catch (IOException) { } }
        _cts.Dispose();
    }
}
