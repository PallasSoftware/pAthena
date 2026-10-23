using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

public enum StorageOperationStatus { Applied, Rejected, Unavailable }

public readonly record struct StorageOperationResult(StorageOperationStatus Status, ContainerPlanner.StorageTransferResult Reason)
{
    public bool Succeeded => Status == StorageOperationStatus.Applied;
    public static StorageOperationResult Ok => new(StorageOperationStatus.Applied, ContainerPlanner.StorageTransferResult.Success);
    public static StorageOperationResult Reject(ContainerPlanner.StorageTransferResult reason) => new(StorageOperationStatus.Rejected, reason);
    public static StorageOperationResult Unavailable => new(StorageOperationStatus.Unavailable, ContainerPlanner.StorageTransferResult.Invalid);
}

// MapServer's own owner of one durable non-inventory container (account Storage or Cart) for the
// current session, parallel to CharacterInventoryService but deliberately NOT sharing its
// CharacterInventorySnapshot/IInventoryRules types - see ContainerPlanner's own doc comment for why
// storage/cart need a genuinely simpler rule set (no weight, no equip-slot concept, no runtime
// SlotIndex/"hole" view). Transfer-in (inventory -> container) and transfer-out (container ->
// inventory) are each ONE atomic CharServer operation spanning both containers - never two separate
// round trips (pinned storage_storageadd's own add-then-delete sequence is explicitly NOT atomic in
// real rAthena; this project does strictly better).
//
// Client projection (open-storage/open-cart request, the storage/cart item-list packets, and the
// transfer request packets themselves) has no capture evidence yet (see ai/iro-2026-wire.md and the
// plan doc's own "storage/cart open and transfer" outstanding-capture entry) - this service is
// fully functional and tested server-side; packet wiring is Protocol-pending-capture.
public sealed class CharacterStorageService
{
    private readonly uint _accountId;
    private readonly uint _charId;
    private readonly ICharacterInventoryOperations _operations;
    private readonly InventoryContainer _container;
    private readonly int _maxSlots;
    private readonly IReadOnlyDictionary<int, ItemDefinition> _items;
    private IReadOnlyList<InventoryItemRow> _rows;

    public CharacterStorageService(uint accountId, uint charId, ICharacterInventoryOperations operations, InventoryContainer container, IReadOnlyList<InventoryItemRow> initial, IReadOnlyDictionary<int, ItemDefinition> items)
    {
        if (container is not (InventoryContainer.Storage or InventoryContainer.Cart))
            throw new ArgumentOutOfRangeException(nameof(container), container, "CharacterStorageService only owns Storage or Cart.");
        _accountId = accountId;
        _charId = charId;
        _operations = operations;
        _container = container;
        _maxSlots = container == InventoryContainer.Cart ? ContainerPlanner.MaxCartSlots : ContainerPlanner.MaxStorageSlots;
        _items = items;
        _rows = initial;
    }

    public IReadOnlyList<InventoryItemRow> Rows => _rows;

    // Moves `amount` of the inventory row identified by `sourceDurableId` into this container, in
    // ONE durable operation together with the inventory-side removal.
    public async Task<StorageOperationResult> TransferInAsync(CharacterInventorySnapshot inventory, uint sourceDurableId, uint amount, CancellationToken cancellationToken)
    {
        var sourceRow = inventory.Items.FirstOrDefault(i => i.DurableId == sourceDurableId);
        if (sourceRow is null) return StorageOperationResult.Reject(ContainerPlanner.StorageTransferResult.Invalid);

        var plan = ContainerPlanner.PlanTransferIn(sourceRow, amount, _rows, _maxSlots, _items, _container);
        if (!plan.Succeeded) return StorageOperationResult.Reject(plan.Result);

        var request = new InventoryOperationRequest(_accountId, _charId, Guid.NewGuid(), InventoryOperationKind.Transfer, plan.Mutations!);
        var response = await _operations.ExecuteAsync(request, cancellationToken);
        if (response is null) return StorageOperationResult.Unavailable;
        if (response.Status is not (InventoryOperationStatus.Applied or InventoryOperationStatus.Replayed))
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Storage transfer-in rejected status={response.Status} accountId={_accountId} charId={_charId} container={_container}.");
            return StorageOperationResult.Reject(ContainerPlanner.StorageTransferResult.Invalid);
        }

        ApplyContainerResults(FilterToContainer(response.Results, request.Mutations, _container));
        return StorageOperationResult.Ok;
    }

    // Moves `amount` of the container row identified by `sourceDurableId` back into the inventory,
    // granted there via the caller-supplied planner (InventoryPlanner.PlanGrants) in the SAME
    // durable operation as this container's own removal - see PlanTransferOutAsync's own doc
    // comment for why the inventory-side capacity decision must be made by the caller.
    public async Task<(StorageOperationResult Result, IReadOnlyList<InventoryMutationResult> InventoryResults)> TransferOutAsync(
        uint sourceDurableId, uint amount, Func<InventoryItemRow, IReadOnlyList<InventoryMutation>?> planInventorySide, CancellationToken cancellationToken)
    {
        var sourceRow = _rows.FirstOrDefault(r => r.DurableId == sourceDurableId);
        if (sourceRow is null || amount == 0 || amount > sourceRow.Amount) return (StorageOperationResult.Reject(ContainerPlanner.StorageTransferResult.Invalid), []);

        var inventoryMutations = planInventorySide(sourceRow);
        if (inventoryMutations is null) return (StorageOperationResult.Reject(ContainerPlanner.StorageTransferResult.Full), []);

        var mutations = new List<InventoryMutation>(inventoryMutations) { ContainerPlanner.PlanTransferOutRemoval(sourceRow, amount, _container) };
        var request = new InventoryOperationRequest(_accountId, _charId, Guid.NewGuid(), InventoryOperationKind.Transfer, mutations);
        var response = await _operations.ExecuteAsync(request, cancellationToken);
        if (response is null) return (StorageOperationResult.Unavailable, []);
        if (response.Status is not (InventoryOperationStatus.Applied or InventoryOperationStatus.Replayed))
            return (StorageOperationResult.Reject(ContainerPlanner.StorageTransferResult.Invalid), []);

        var containerResults = new List<InventoryMutationResult>();
        var inventoryResults = new List<InventoryMutationResult>();
        for (var i = 0; i < request.Mutations.Count; i++)
        {
            if (request.Mutations[i].Container == _container) containerResults.Add(response.Results[i]);
            else inventoryResults.Add(response.Results[i]);
        }
        ApplyContainerResults(containerResults);
        return (StorageOperationResult.Ok, inventoryResults);
    }

    private static IReadOnlyList<InventoryMutationResult> FilterToContainer(IReadOnlyList<InventoryMutationResult> results, IReadOnlyList<InventoryMutation> mutations, InventoryContainer container)
    {
        var filtered = new List<InventoryMutationResult>();
        for (var i = 0; i < mutations.Count; i++)
            if (mutations[i].Container == container) filtered.Add(results[i]);
        return filtered;
    }

    private void ApplyContainerResults(IReadOnlyList<InventoryMutationResult> results)
    {
        var rows = _rows.ToList();
        foreach (var result in results)
        {
            var index = rows.FindIndex(r => r.DurableId == result.DurableId);
            if (result.Deleted) { if (index >= 0) rows.RemoveAt(index); }
            else if (index >= 0) rows[index] = result.Row!;
            else rows.Add(result.Row!);
        }
        _rows = rows;
    }
}
