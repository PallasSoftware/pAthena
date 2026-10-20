using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.Net;

// Pinned pc_inventory_rentals (pc.cpp:1200-1225), called at login/reconnect - see
// RentalExpiryResolver's own doc comment. Real socket path (RunAsync/CompleteIroAuthenticationAsync),
// real InMemoryInventoryOperations (CharServer's actual idempotent operation rules) - only the
// clock is a test double, so "now" is deterministic.
public sealed class MapClientSessionRentalExpiryTests
{
    private const uint AccountId = 5_000_000;
    private const uint CharId = 17;

    private static readonly CharacterGameplayState FullHealthNovice = new(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(u);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CharacterInventoryItem RentalRow(uint durableId, uint slotIndex, int itemId, uint expireTime) =>
        new(durableId, slotIndex, itemId, Amount: 1, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0,
            Detail: new ItemInstanceDetail(0, 0, 0, 0, 0, 0, default, default, default, default, default, expireTime, 0, 0, 0));

    [Fact]
    public async Task Login_WithAnAlreadyExpiredRental_DurablyRemovesIt()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredAt = (uint)now.AddMinutes(-5).ToUnixTimeSeconds();
        var initialInventory = new CharacterInventorySnapshot([
            RentalRow(1, 0, 11518, expiredAt), // BluePotion, rental, already expired
        ]);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var inventoryPersistence = new InMemoryInventoryOperations(initialInventory);
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: new StubGameplayStatePersistence(FullHealthNovice), inventoryOperations: inventoryPersistence,
            accountId: AccountId, charId: CharId, timeProvider: new FixedTimeProvider(now));
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        Assert.Empty(session.Inventory!.Items); // The expired rental is gone from the live runtime snapshot.
        Assert.Equal(0, inventoryPersistence.AmountOf(11518)); // ...and durably removed, not merely hidden client-side.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Login_WithAStillValidRental_LeavesItUntouched()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresLater = (uint)now.AddHours(1).ToUnixTimeSeconds();
        var initialInventory = new CharacterInventorySnapshot([
            RentalRow(1, 0, 11518, expiresLater),
        ]);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var inventoryPersistence = new InMemoryInventoryOperations(initialInventory);
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: new StubGameplayStatePersistence(FullHealthNovice), inventoryOperations: inventoryPersistence,
            accountId: AccountId, charId: CharId, timeProvider: new FixedTimeProvider(now));
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        Assert.Single(session.Inventory!.Items);
        Assert.Equal(1, inventoryPersistence.AmountOf(11518));
        Assert.Empty(inventoryPersistence.Applied); // No durable mutation was even attempted.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
