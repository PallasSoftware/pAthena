using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned pc_insert_card (pc.cpp:5596-5652): consumes one card item and writes its id into the
// target equip row's first free card slot, in one atomic operation (pinned source's own delete-
// then-update is not atomic in real rAthena; this project does strictly better, matching the
// precedent set by ContainerPlanner's own transfer-in/out). CharServer remains the durable owner.
// Only the mutation itself is modeled - the compound-UI trigger and its success/failure client
// packets are Protocol-pending-capture (see ItemCardInsertResolver's own doc comment).
public sealed class CharacterItemCardService(CharacterInventoryService inventoryService)
{
    public async Task<(bool Succeeded, CardInsertFailureReason? Failure, CharacterInventorySnapshot? Updated)> InsertCardAsync(
        uint equipDurableId, uint cardDurableId, IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken)
    {
        CardInsertFailureReason? decided = null;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Card, inventory =>
        {
            var equipRow = inventory.Items.FirstOrDefault(i => i.DurableId == equipDurableId);
            var cardRow = inventory.Items.FirstOrDefault(i => i.DurableId == cardDurableId);
            if (equipRow is null) { decided = CardInsertFailureReason.UnknownEquipRow; return null; }
            if (cardRow is null) { decided = CardInsertFailureReason.UnknownCardRow; return null; }

            items.TryGetValue(equipRow.ItemId, out var equipTarget);
            items.TryGetValue(cardRow.ItemId, out var card);
            if (!ItemCardInsertResolver.TryResolve(equipTarget, equipRow, card, cardRow, out var slot, out var failure))
            { decided = failure; return null; }

            var cardId = (uint)cardRow.ItemId;
            var row = equipRow.ToRow();
            var updatedRow = slot switch
            {
                0 => row with { Card0 = cardId },
                1 => row with { Card1 = cardId },
                2 => row with { Card2 = cardId },
                _ => row with { Card3 = cardId },
            };

            return (IReadOnlyList<InventoryMutation>)
            [
                new RemoveFromRowMutation(InventoryContainer.Inventory, cardRow.DurableId, 1, cardRow.Amount, cardRow.Details.Revision),
                new UpdateRowMutation(InventoryContainer.Inventory, equipRow.DurableId, equipRow.Details.Revision, updatedRow),
            ];
        }, cancellationToken);

        if (decided is not null) return (false, decided, null);
        return change.Succeeded ? (true, null, change.Snapshot) : (false, CardInsertFailureReason.PersistenceFailed, null);
    }
}
