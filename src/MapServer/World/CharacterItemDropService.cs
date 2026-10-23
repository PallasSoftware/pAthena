using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Pinned pc_dropitem (pc.cpp) with pc_candrop (pc.cpp:11356-11369): a player puts some of one inventory row on the ground,
// where anyone may pick it up (map_addflooritem with no first/second/third owner). Two authorities are involved -
// CharServer owns the row, World owns the ground item - and there is no cross-store transaction, so the order is chosen to
// never duplicate an item: the durable removal commits FIRST (operation id = the drop id), then World drops with the same
// drop id (World answers AlreadyDropped for a retry). A drop World definitively refuses (MapNotFound) is compensated by
// granting the same instance back under an id derived from the drop; an unanswered World drop is retried, and if it stays
// unanswered the item is lost - the same bounded-loss policy ground items already have (World restart, abandoned claim).
// Pinned does it in the other order inside one process (addflooritem, then delitem), which cannot fail between the steps.
//
// Not modeled: the MF_NODROP map flag (no map-flag runtime yet; shortcut below), sc.cant.drop, map_search_freecell_dist's
// scatter onto a free neighbouring cell (the item lands on the player's cell), and the hatched-pet-egg rule (pets are not
// modeled). The client wire (CZ_ITEM_THROW request, the 0x00AF delete ack) is Protocol-pending-capture.
public sealed class CharacterItemDropService(CharacterInventoryService inventoryService, IWorldRuntime world, int worldAttempts = 3)
{
    public enum DropResult { Dropped, UnknownRow, InvalidAmount, Equipped, RegisteredForEquipSwitch, NotDroppable, PersistenceFailed, Refused, WorldUnavailable }

    // Pinned conf/groups.yml group 0 (every new account): level 0, can_trade, no can_trade_bounded.
    private const int PlayerGroupLevel = 0;

    public async Task<(DropResult Result, uint? GroundItemId)> DropAsync(uint slotIndex, uint amount, string mapId, ushort x, ushort y,
        IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken)
    {
        var dropId = Guid.NewGuid();
        var decided = DropResult.UnknownRow;
        InventoryItemRow? instance = null;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Drop, inventory =>
        {
            var row = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
            decided = Check(row, amount, items);
            if (decided != DropResult.Dropped) return null;
            instance = row!.ToRow() with { DurableId = 0, Amount = amount, Equip = 0, EquipSwitch = 0, Favorite = 0, Revision = 0 };
            return [InventoryPlanner.RemoveFromRow(row, amount)];
        }, cancellationToken, operationId: dropId);

        if (decided != DropResult.Dropped) return (decided, null);
        if (!change.Succeeded) return (DropResult.PersistenceFailed, null);

        var bytes = new byte[InventoryItemRow.WireLength];
        instance!.Write(bytes);
        var drop = new WorldGroundItemDrop(mapId, x, y, new WorldGroundItemContent((int)instance.ItemId, amount, InstanceRow: bytes), new WorldGroundItemOwnership(null), dropId);
        for (var attempt = 1; attempt <= worldAttempts; attempt++)
        {
            try
            {
                var result = await world.DropGroundItemAsync(drop, cancellationToken);
                if (result.Status is WorldGroundItemDropStatus.Dropped or WorldGroundItemDropStatus.AlreadyDropped) return (DropResult.Dropped, result.GroundItemId);
            }
            catch (Exception ex) when (WorldRpcFailureClassifier.IsTransientWorldRpcFailure(ex)) { continue; }
            // A deterministic refusal (MapNotFound, or the grain rejecting a map it does not own - RequireOwnedMap throws before
            // anything is created): nothing is on the ground, so the removed units go back, once, by a drop-derived operation id.
            catch (Exception ex) when (ex is not OperationCanceledException) { MapLogger.Warning($"[ITEM DROP] World refused drop {dropId}: {ex.GetType().Name}: {ex.Message}"); }
            await inventoryService.GrantAsync([new InventoryPlanner.GrantRequest(items[(int)instance.ItemId], amount, instance)], InventoryOperationKind.Drop,
                cancellationToken, CompensationId(dropId));
            return (DropResult.Refused, null);
        }
        return (DropResult.WorldUnavailable, null);
    }

    // pc_dropitem's own refusals, in pinned order, then pc_candrop for a group-0 player.
    private static DropResult Check(CharacterInventoryItem? row, uint amount, IReadOnlyDictionary<int, ItemDefinition> items)
    {
        if (row is null || !items.TryGetValue(row.ItemId, out var definition)) return DropResult.UnknownRow;
        if (amount == 0 || amount > row.Amount) return DropResult.InvalidAmount;
        // Divergence: pinned pc_delitem would unequip a worn row; the stock client never offers it, so it is refused here.
        if (row.Equip != 0) return DropResult.Equipped;
        if (row.Details.EquipSwitch != 0) return DropResult.RegisteredForEquipSwitch;
        // shortcut: MF_NODROP is not checked - no map-flag runtime exists yet; add it with that runtime.
        if (row.Details.ExpireTime != 0 || row.Bound != 0) return DropResult.NotDroppable; // rental; bound without can_trade_bounded
        if (definition.Meta is { NoDrop: true } meta && PlayerGroupLevel < meta.TradeOverride) return DropResult.NotDroppable;
        return DropResult.Dropped;
    }

    // The row a pickup grants for a ground item's content: the dropped instance when one was carried (pinned
    // map_addflooritem copies the whole struct item; pc_additem then clears equip/equipSwitch, pc.cpp:6045-6054), else none
    // (fresh loot gets a new row). A malformed instance is ignored rather than trusted.
    public static InventoryItemRow? PickupTemplate(WorldGroundItemContent content) =>
        content.InstanceRow is { Length: InventoryItemRow.WireLength } instanceRow
            ? InventoryItemRow.Read(instanceRow) with { DurableId = 0, Amount = content.Amount, Equip = 0, EquipSwitch = 0, Revision = 0 }
            : null;

    private static Guid CompensationId(Guid dropId)
    {
        var bytes = dropId.ToByteArray();
        bytes[0] ^= 0xA5; // deterministic and distinct from the removal's own operation id
        return new Guid(bytes);
    }
}
