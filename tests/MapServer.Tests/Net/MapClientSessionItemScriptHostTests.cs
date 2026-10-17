using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.Shared.Inventory;
using MapServer.Tests.Testing;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

// Generic INpcScriptHost.CountItemAsync/DeleteItemAsync/GetItemAsync tests, exercised directly
// against the real MapClientSession implementation (cast to INpcScriptHost, matching how a
// generated script's ScriptContext actually calls it) rather than through a generated script or
// the wire. Uses arbitrary test item IDs (never Sailor's 6008/611 magic constants) to prove these
// three capabilities are genuinely generic, not Sailor-specific - the Sailor-specific wire-level
// scenario is covered separately by GeneratedSailorIntegrationTests.
//
// Reuses the exact RecordingInventoryPersistence/RecordingInventoryListPersistence fakes already
// established in MapClientSessionUseItemTests (same multi-row-capable persistence-double shape),
// so this file does not invent a second competing fake pattern.
public sealed class MapClientSessionItemScriptHostTests
{
    private const uint AccountId = 3_000_000;
    private const uint CharId = 11;
    private const int TestItemId = 55001;
    private const int OtherItemId = 55002;

    private static async Task<(TcpClient Client, MapClientSession Session, InMemoryInventoryOperations Persistence, Task RunTask)> SetupAsync(
        CharacterInventorySnapshot initialInventory)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var gameplayState = new CharacterGameplayState(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        var gameplayPersistence = new FixedGameplayStatePersistence(gameplayState);
        var inventoryPersistence = new InMemoryInventoryOperations(initialInventory);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: gameplayPersistence, inventoryOperations: inventoryPersistence,
            accountId: AccountId, charId: CharId);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        return (client, session, inventoryPersistence, run);
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(u);
    }

    private static async Task DisposeAsync(TcpClient client, MapClientSession session, Task run)
    {
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task CountItemAsync_SumsAcrossMultipleMatchingStacks()
    {
        var initial = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, TestItemId, 3, 0, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, TestItemId, 4, 0, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 3, SlotIndex: 2, OtherItemId, 100, 0, true, 0, 0, 0),
        ]);
        var (client, session, _, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        var count = await host.CountItemAsync(TestItemId, CancellationToken.None);

        Assert.Equal(7u, count); // 3 + 4, never conflated with the unrelated OtherItemId(100) row.
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task CountItemAsync_NoMatchingRows_ReturnsZero()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, OtherItemId, 5, 0, true, 0, 0, 0)]);
        var (client, session, _, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        var count = await host.CountItemAsync(TestItemId, CancellationToken.None);

        Assert.Equal(0u, count);
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task DeleteItemAsync_InsufficientQuantity_FailsAndConsumesNothing()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, TestItemId, 1, 0, true, 0, 0, 0)]);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        var result = await host.DeleteItemAsync(TestItemId, 2, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(persistence.Requests); // Insufficient quantity is rejected while planning: nothing is ever sent.
        Assert.Equal(1u, await host.CountItemAsync(TestItemId, CancellationToken.None)); // Nothing changed.
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task DeleteItemAsync_SufficientSingleStack_ConsumesAndUpdatesRuntimeSnapshot()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, TestItemId, 5, 0, true, 0, 0, 0)]);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        var result = await host.DeleteItemAsync(TestItemId, 2, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3u, await host.CountItemAsync(TestItemId, CancellationToken.None));
        Assert.Equal(3u, session.Inventory!.Items.Single(i => i.ItemId == TestItemId).Amount);
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task DeleteItemAsync_ExactStackAmount_DeletesRowEntirely()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, TestItemId, 2, 0, true, 0, 0, 0)]);
        var (client, session, _, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        var result = await host.DeleteItemAsync(TestItemId, 2, CancellationToken.None);

        Assert.True(result);
        Assert.DoesNotContain(session.Inventory!.Items, i => i.ItemId == TestItemId);
        Assert.Equal(0u, await host.CountItemAsync(TestItemId, CancellationToken.None));
        await DisposeAsync(client, session, run);
    }

    // Proves the multi-row consumption path this task's spec explicitly requires: a stackable
    // item split across more than one durable row must be summed for the sufficiency check and
    // consumed across rows in stable ascending-DurableId order until satisfied, never assuming
    // (or requiring) a single-stack layout.
    [Fact]
    public async Task DeleteItemAsync_SpansMultipleStacks_ConsumesInAscendingDurableIdOrder()
    {
        var initial = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, TestItemId, 2, 0, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, TestItemId, 3, 0, true, 0, 0, 0),
        ]);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        var result = await host.DeleteItemAsync(TestItemId, 4, CancellationToken.None);

        Assert.True(result);
        // DurableId 1 (2 units) fully consumed first, then 2 units from DurableId 2 (leaving 1) - all in ONE durable operation, so a
        // failure part way can no longer leave a half-consumed delitem behind.
        var operation = Assert.Single(persistence.Applied);
        Assert.Equal([(1u, 2u), (2u, 2u)], operation.Mutations.Cast<RemoveFromRowMutation>().Select(m => (m.DurableId, m.Amount)));
        Assert.DoesNotContain(session.Inventory!.Items, i => i.DurableId == 1);
        Assert.Equal(1u, session.Inventory!.Items.Single(i => i.DurableId == 2).Amount);
        Assert.Equal(1u, await host.CountItemAsync(TestItemId, CancellationToken.None));
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task DeleteItemAsync_PersistenceFailure_ReportsFailure_NotFakeSuccess()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, TestItemId, 5, 0, true, 0, 0, 0)]);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;
        persistence.DropRequests = 100; // CharServer never confirms the operation

        var result = await host.DeleteItemAsync(TestItemId, 2, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(5u, await host.CountItemAsync(TestItemId, CancellationToken.None)); // Runtime snapshot left unchanged on failure.
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task DeleteItemAsync_NoInventoryLoaded_FailsWithoutThrowing()
    {
        // Constructs a session that never completes authentication, so _inventory stays null -
        // proving DeleteItemAsync fails closed rather than throwing/crashing a generated script.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        await using var session = new MapClientSession(1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true);
        var host = (INpcScriptHost)session;

        var result = await host.DeleteItemAsync(TestItemId, 1, CancellationToken.None);

        Assert.False(result);
        client.Close();
    }

    [Fact]
    public async Task GetItemAsync_UnregisteredItemId_SkipsWithoutMutationOrThrow()
    {
        var initial = new CharacterInventorySnapshot([]);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        await host.GetItemAsync(itemId: -1, amount: 5, CancellationToken.None); // No such generated item.

        Assert.Empty(persistence.Requests);
        Assert.Empty(session.Inventory!.Items);
        await DisposeAsync(client, session, run);
    }

    // Uses a real generated item (Wood, 6008) rather than TestItemId/OtherItemId here specifically
    // because GetItemAsync must resolve through the real GeneratedItems.ById registry - this one
    // assertion intentionally proves that registry integration, while every other test in this
    // file proves the surrounding logic is item-id-agnostic.
    [Fact]
    public async Task GetItemAsync_RegisteredItem_AddsPersistsAndSynchronizesRuntimeSnapshot()
    {
        var initial = new CharacterInventorySnapshot([]);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        await host.GetItemAsync(itemId: 6008, amount: 5, CancellationToken.None);

        var add = Assert.IsType<AddNewRowMutation>(Assert.Single(Assert.Single(persistence.Applied).Mutations));
        Assert.Equal((6008u, 5u), (add.Row.ItemId, add.Row.Amount));
        var row = Assert.Single(session.Inventory!.Items);
        Assert.Equal(6008, row.ItemId);
        Assert.Equal(5u, row.Amount);
        Assert.Equal(5u, await host.CountItemAsync(6008, CancellationToken.None));
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task GetItemAsync_PersistenceFailure_DoesNotMutateRuntimeSnapshot()
    {
        var initial = new CharacterInventorySnapshot([]);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;
        persistence.DropRequests = 100; // CharServer never confirms the operation

        await host.GetItemAsync(itemId: 6008, amount: 5, CancellationToken.None);

        Assert.Empty(session.Inventory!.Items);
        await DisposeAsync(client, session, run);
    }
}
