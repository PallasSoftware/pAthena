using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// Storage/cart transfer, reusing CharServer's real cross-container atomic/idempotent operation
// rules (InventoryOperationEngine) via InMemoryInventoryOperations - never a hand-rolled stand-in.
// Client-facing packets are Protocol-pending-capture (see CharacterStorageService's own doc
// comment); these tests prove the server-authoritative transfer behavior only.
public sealed class CharacterStorageServiceTests
{
    private const uint AccountId = 1;
    private const uint CharId = 1;
    private static readonly ItemDefinition Wood = ItemCatalog.Current.ByAegisName["Wood"]; // Stackable Etc item.
    private static readonly ItemDefinition Knife = ItemCatalog.Current.ByAegisName["Knife"]; // Non-stackable weapon.

    private static CharacterInventorySnapshot InventoryWith(params CharacterInventoryItem[] items) => new(items);

    [Fact]
    public async Task TransferIn_StackableItem_NoExistingStorageRow_CreatesANewStorageRow()
    {
        var inventory = InventoryWith(new CharacterInventoryItem(1, 0, Wood.Id, 10, 0, true, 0, 0, 0));
        var ops = new InMemoryInventoryOperations(inventory);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Storage, [], ItemCatalog.Current.ById);

        var result = await service.TransferInAsync(inventory, sourceDurableId: 1, amount: 4, CancellationToken.None);

        Assert.True(result.Succeeded);
        var row = Assert.Single(service.Rows);
        Assert.Equal((uint)Wood.Id, row.ItemId);
        Assert.Equal(4u, row.Amount);
        Assert.Equal(6, ops.AmountOf(Wood.Id)); // Inventory side correctly decremented in the SAME operation.
    }

    [Fact]
    public async Task TransferIn_StackableItem_ExistingMatchingStorageRow_MergesIntoIt()
    {
        var inventory = InventoryWith(new CharacterInventoryItem(1, 0, Wood.Id, 10, 0, true, 0, 0, 0));
        var ops = new InMemoryInventoryOperations(inventory);
        ops.SeedContainer(InventoryContainer.Storage, [InventoryItemRow.New((uint)Wood.Id, 5) with { DurableId = 100, Revision = 1 }]);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Storage, ops.ContainerRows(InventoryContainer.Storage), ItemCatalog.Current.ById);

        var result = await service.TransferInAsync(inventory, sourceDurableId: 1, amount: 3, CancellationToken.None);

        Assert.True(result.Succeeded);
        var row = Assert.Single(service.Rows);
        Assert.Equal(8u, row.Amount); // 5 + 3, merged into the existing row, never a second row.
    }

    [Fact]
    public async Task TransferIn_EquippedRow_IsRejected_NeverMutatesAnything()
    {
        var inventory = InventoryWith(new CharacterInventoryItem(1, 0, Knife.Id, 1, 0x000002, true, 0, 0, 0)); // Equipped.
        var ops = new InMemoryInventoryOperations(inventory);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Storage, [], ItemCatalog.Current.ById);

        var result = await service.TransferInAsync(inventory, sourceDurableId: 1, amount: 1, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerPlanner.StorageTransferResult.NotStorable, result.Reason);
        Assert.Empty(ops.Requests);
        Assert.Empty(service.Rows);
    }

    [Fact]
    public async Task TransferIn_WhenContainerIsFull_IsRejected()
    {
        var inventory = InventoryWith(new CharacterInventoryItem(1, 0, Knife.Id, 1, 0, true, 0, 0, 0));
        var fullStorage = Enumerable.Range(1, ContainerPlanner.MaxStorageSlots)
            .Select(i => InventoryItemRow.New((uint)Knife.Id, 1) with { DurableId = (uint)i }).ToArray();
        var ops = new InMemoryInventoryOperations(inventory);
        ops.SeedContainer(InventoryContainer.Storage, fullStorage);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Storage, fullStorage, ItemCatalog.Current.ById);

        var result = await service.TransferInAsync(inventory, sourceDurableId: 1, amount: 1, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerPlanner.StorageTransferResult.Full, result.Reason);
    }

    [Fact]
    public async Task TransferOut_MovesFromStorageBackIntoInventory_InOneDurableOperation()
    {
        var ops = new InMemoryInventoryOperations();
        ops.SeedContainer(InventoryContainer.Storage, [InventoryItemRow.New((uint)Wood.Id, 7) with { DurableId = 100, Revision = 1 }]);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Storage, ops.ContainerRows(InventoryContainer.Storage), ItemCatalog.Current.ById);
        var storageDurableId = service.Rows[0].DurableId;

        var (result, inventoryResults) = await service.TransferOutAsync(storageDurableId, amount: 3, sourceRow =>
            [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(sourceRow.ItemId, 3))], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(inventoryResults);
        Assert.Equal(4u, service.Rows[0].Amount); // 7 - 3, still present (not fully drained).
        Assert.Equal(3, ops.AmountOf(Wood.Id)); // Landed in the real inventory container.
    }

    [Fact]
    public async Task TransferOut_FullyDrainsTheStorageRow_RemovesIt()
    {
        var ops = new InMemoryInventoryOperations();
        ops.SeedContainer(InventoryContainer.Storage, [InventoryItemRow.New((uint)Wood.Id, 3) with { DurableId = 100, Revision = 1 }]);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Storage, ops.ContainerRows(InventoryContainer.Storage), ItemCatalog.Current.ById);
        var storageDurableId = service.Rows[0].DurableId;

        var (result, _) = await service.TransferOutAsync(storageDurableId, amount: 3, sourceRow =>
            [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(sourceRow.ItemId, 3))], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(service.Rows);
    }

    [Fact]
    public async Task TransferOut_WhenInventorySideDeclines_RejectsTheWholeOperation_StorageRowUntouched()
    {
        var ops = new InMemoryInventoryOperations();
        ops.SeedContainer(InventoryContainer.Storage, [InventoryItemRow.New((uint)Wood.Id, 5) with { DurableId = 100, Revision = 1 }]);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Storage, ops.ContainerRows(InventoryContainer.Storage), ItemCatalog.Current.ById);
        var storageDurableId = service.Rows[0].DurableId;

        var (result, _) = await service.TransferOutAsync(storageDurableId, amount: 5, _ => null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(5u, service.Rows[0].Amount); // Untouched - the inventory side declined before any request was sent.
        Assert.Empty(ops.Requests);
    }

    [Fact]
    public async Task CartContainer_UsesItsOwnSmallerCap_IndependentOfStorage()
    {
        var inventory = InventoryWith(new CharacterInventoryItem(1, 0, Knife.Id, 1, 0, true, 0, 0, 0));
        var fullCart = Enumerable.Range(1, ContainerPlanner.MaxCartSlots)
            .Select(i => InventoryItemRow.New((uint)Knife.Id, 1) with { DurableId = (uint)i }).ToArray();
        var ops = new InMemoryInventoryOperations(inventory);
        ops.SeedContainer(InventoryContainer.Cart, fullCart);
        var service = new CharacterStorageService(AccountId, CharId, ops, InventoryContainer.Cart, fullCart, ItemCatalog.Current.ById);

        var result = await service.TransferInAsync(inventory, sourceDurableId: 1, amount: 1, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerPlanner.StorageTransferResult.Full, result.Reason); // MaxCartSlots (100), not MaxStorageSlots (600).
    }
}
