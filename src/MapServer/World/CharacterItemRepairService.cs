using Athena.Net.MapServer.Net;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned BUILDIN_FUNC(repair)/BUILDIN_FUNC(repairall) (script.cpp:9452-9500): clears one broken
// item's attribute back to 0. CharServer remains the durable owner; this is one atomic inventory
// operation, matching CharacterEquipmentMutationService's own shape. Only the mutation itself is
// modeled here - see ItemRepairResolver's own doc comment for why the player-facing repair-list
// UI/trigger is not: that needs a client packet with no capture evidence yet.
public sealed class CharacterItemRepairService(CharacterInventoryService inventoryService)
{
    public enum RepairResult { Success, NotBroken, UnknownRow, PersistenceFailed }

    public async Task<(RepairResult Result, CharacterInventorySnapshot? Updated)> RepairAsync(uint durableId, CancellationToken cancellationToken)
    {
        RepairResult decided = RepairResult.UnknownRow;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Repair, inventory =>
        {
            var target = inventory.Items.FirstOrDefault(i => i.DurableId == durableId);
            if (target is null) { decided = RepairResult.UnknownRow; return null; }
            if (!target.Details.IsBroken) { decided = RepairResult.NotBroken; return null; }
            decided = RepairResult.Success;
            return (IReadOnlyList<InventoryMutation>)[new UpdateRowMutation(InventoryContainer.Inventory, target.DurableId, target.Details.Revision, target.ToRow() with { Attribute = 0 })];
        }, cancellationToken);

        if (decided != RepairResult.Success) return (decided, null);
        return change.Succeeded ? (RepairResult.Success, change.Snapshot) : (RepairResult.PersistenceFailed, null);
    }
}
