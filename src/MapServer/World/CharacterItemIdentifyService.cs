using Athena.Net.MapServer.Net;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned pc_identifyall (pc.cpp:5664-5679): sets every currently-unidentified row's identify flag
// to 1, in one atomic operation. CharServer remains the durable owner; this is the identify
// counterpart to CharacterItemRepairService's own shape. Only the mutation itself is modeled - see
// ItemIdentifyResolver's own doc comment for why the per-item identify-list UI/trigger is not
// (needs a client packet with no capture evidence yet).
public sealed class CharacterItemIdentifyService(CharacterInventoryService inventoryService)
{
    public enum IdentifyAllResult { Success, NothingToIdentify, PersistenceFailed }

    public async Task<(IdentifyAllResult Result, CharacterInventorySnapshot? Updated)> IdentifyAllAsync(CancellationToken cancellationToken)
    {
        var decided = IdentifyAllResult.NothingToIdentify;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Identify, inventory =>
        {
            var unidentified = ItemIdentifyResolver.FindUnidentified(inventory);
            if (unidentified.Count == 0) { decided = IdentifyAllResult.NothingToIdentify; return null; }
            decided = IdentifyAllResult.Success;
            return (IReadOnlyList<InventoryMutation>)[.. unidentified.Select(item =>
            {
                var row = inventory.Items.First(i => i.DurableId == item.DurableId);
                return new UpdateRowMutation(InventoryContainer.Inventory, row.DurableId, row.Details.Revision, row.ToRow() with { Identify = 1 });
            })];
        }, cancellationToken);

        if (decided != IdentifyAllResult.Success) return (decided, null);
        return change.Succeeded ? (IdentifyAllResult.Success, change.Snapshot) : (IdentifyAllResult.PersistenceFailed, null);
    }
}
