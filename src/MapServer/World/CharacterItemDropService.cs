using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Pinned pc_dropitem (pc.cpp) with pc_candrop (pc.cpp:11356-11369): a player puts some of one inventory row on the ground,
// where anyone may pick it up (map_addflooritem with no first/second/third owner). Pinned does addflooritem then delitem in
// one process; here two authorities are involved - CharServer owns the units, World the ground item - with no shared
// transaction, so the drop is a transactional outbox and never destroys or duplicates the units:
//
//   1. One CharServer operation moves the units from the inventory into a PendingGroundDrop row AND writes the pending-drop
//      record (drop id, map, cell, World epoch observed just before). The units stay durable, player-owned rows throughout.
//   2. World drops under that drop id (idempotent; Resolvable, so World remembers the id long after the item is gone).
//   3. The drop is SETTLED by exactly one more CharServer operation, which also deletes the record and is refused once it is
//      gone: "complete" consumes the pending units when World has the ground item; "return" puts them back in the inventory
//      when World has FENCED the drop id (ResolveGroundItemDropAsync), so no late copy of the drop request can create it.
//      An ambiguous World answer is never compensated on a guess - it is resolved (dropped, or fenced then returned).
//   4. Anything left unsettled (CharServer or World unreachable, a MapServer crash between the steps) stays in the outbox and
//      is settled by RecoverAsync, which the session runs when the character enters the map.
// A World restart (new ground-item epoch) between the observation and the resolution makes "dropped, then lost with the old
// epoch" indistinguishable from "never dropped"; that drop is completed as lost - the documented policy for every ground
// item at a World restart - rather than returned and possibly duplicated, and it is logged as an error, never silently.
//
// Not modeled: sc.cant.drop, map_search_freecell_dist's scatter onto a neighbouring cell (the item lands on the player's
// cell), and the hatched-pet-egg rule (pets are not modeled). The client wire (CZ_ITEM_THROW, the 0x00AF delete ack) is
// Protocol-pending-capture.
public sealed class CharacterItemDropService(CharacterInventoryService inventoryService, IWorldRuntime world, int worldAttempts = 3)
{
    // Dropped: on the ground. Returned: World refused it, the units are back in the inventory. Pending: the units are held in
    // the outbox and will be settled by RecoverAsync. WorldUnavailable/PersistenceFailed: nothing changed. LostToWorldRestart:
    // settled as lost because World restarted before confirming it (see the type comment).
    public enum DropResult { Dropped, UnknownRow, InvalidAmount, Equipped, RegisteredForEquipSwitch, NotDroppable, NoDropMap, PersistenceFailed, WorldUnavailable, Returned, Pending, LostToWorldRestart }

    // Pinned conf/groups.yml group 0 (every new account): level 0, can_trade, no can_trade_bounded.
    private const int PlayerGroupLevel = 0;

    public async Task<(DropResult Result, uint? GroundItemId)> DropAsync(uint slotIndex, uint amount, string mapId, ushort x, ushort y,
        IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken, bool noDropMap = false)
    {
        // Pinned pc_dropitem checks MF_NODROP before pc_candrop.
        if (noDropMap) return (DropResult.NoDropMap, null);
        var precheck = Check(inventoryService.Snapshot.Items.FirstOrDefault(i => i.SlotIndex == slotIndex), amount, items);
        if (precheck != DropResult.Dropped) return (precheck, null);

        // The epoch is observed before anything moves: World being unreachable here costs nothing.
        Guid epoch;
        try { epoch = (await world.GetGroundItemsAsync(mapId, cancellationToken)).Epoch.Value; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (DropResult.WorldUnavailable, null); }

        var record = new PendingGroundDrop(Guid.NewGuid(), mapId, x, y, epoch);
        var decided = DropResult.UnknownRow;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Drop, inventory =>
        {
            var row = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
            decided = Check(row, amount, items);
            if (decided != DropResult.Dropped) return null;
            var instance = row!.ToRow() with { DurableId = 0, Amount = amount, Equip = 0, EquipSwitch = 0, Favorite = 0, Revision = 0 };
            return [InventoryPlanner.RemoveFromRow(row, amount), new AddNewRowMutation(InventoryContainer.PendingGroundDrop, instance)];
        }, cancellationToken, operationId: record.DropId, recordPendingDrop: record);

        if (decided != DropResult.Dropped) return (decided, null);
        if (change.Status == InventoryChangeStatus.InDoubt) return (DropResult.Pending, null); // may be in the outbox: RecoverAsync settles it
        if (!change.Succeeded) return (DropResult.PersistenceFailed, null);

        var pendingRow = change.Results[1].Row!;
        return await SettleAsync(record with { ItemRowId = pendingRow.DurableId }, pendingRow, items, dropFirst: true, cancellationToken);
    }

    // Settles every unsettled drop of this character (after a crash, an outage or an in-doubt operation). Resolution only - an
    // old drop is never re-dropped somewhere the player no longer is: either World has it, or it is fenced and returned.
    public async Task<IReadOnlyList<(Guid DropId, DropResult Result)>> RecoverAsync(IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken)
    {
        var pending = await inventoryService.GetPendingGroundDropsAsync(cancellationToken);
        if (pending is null) return [];
        var outcomes = new List<(Guid, DropResult)>();
        foreach (var (record, row) in pending)
            outcomes.Add((record.DropId, (await SettleAsync(record, row, items, dropFirst: false, cancellationToken)).Result));
        return outcomes;
    }

    private async Task<(DropResult Result, uint? GroundItemId)> SettleAsync(PendingGroundDrop record, InventoryItemRow pendingRow,
        IReadOnlyDictionary<int, ItemDefinition> items, bool dropFirst, CancellationToken cancellationToken)
    {
        if (dropFirst)
        {
            var bytes = new byte[InventoryItemRow.WireLength];
            pendingRow.Write(bytes);
            var drop = new WorldGroundItemDrop(record.MapId, record.X, record.Y, new WorldGroundItemContent((int)pendingRow.ItemId, pendingRow.Amount, InstanceRow: bytes),
                new WorldGroundItemOwnership(null), record.DropId, Resolvable: true);
            for (var attempt = 1; attempt <= worldAttempts; attempt++)
            {
                try
                {
                    var result = await world.DropGroundItemAsync(drop, cancellationToken);
                    if (result.Status is WorldGroundItemDropStatus.Dropped or WorldGroundItemDropStatus.AlreadyDropped)
                        return await CompleteAsync(record, pendingRow, result.GroundItemId, cancellationToken);
                    break; // a definitive "not dropped" is still settled through the fence below
                }
                catch (Exception ex) when (WorldRpcFailureClassifier.IsTransientWorldRpcFailure(ex)) { }
                catch (Exception ex) when (ex is not OperationCanceledException) { MapLogger.Warning($"[ITEM DROP] World refused drop {record.DropId}: {ex.GetType().Name}: {ex.Message}"); break; }
            }
        }

        WorldGroundItemDropResolutionResult resolution;
        try { resolution = await world.ResolveGroundItemDropAsync(new WorldGroundItemDropResolution(record.MapId, record.DropId, record.WorldEpoch), cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MapLogger.Warning($"[ITEM DROP] Drop {record.DropId} left in the outbox: World resolution failed ({ex.GetType().Name}).");
            return (DropResult.Pending, null);
        }

        switch (resolution.Status)
        {
            case WorldGroundItemDropResolutionStatus.Dropped:
                return await CompleteAsync(record, pendingRow, resolution.GroundItemId, cancellationToken);
            case WorldGroundItemDropResolutionStatus.Fenced:
                return await ReturnAsync(record, pendingRow, items, cancellationToken);
            default:
                MapLogger.Error($"[ITEM DROP] Drop {record.DropId} of itemId={pendingRow.ItemId} x{pendingRow.Amount} on {record.MapId}: World restarted before the drop was confirmed; settled as lost with the old ground-item epoch.");
                var (completed, _) = await CompleteAsync(record, pendingRow, null, cancellationToken);
                return (completed == DropResult.Dropped ? DropResult.LostToWorldRestart : completed, null);
        }
    }

    private async Task<(DropResult, uint?)> CompleteAsync(PendingGroundDrop record, InventoryItemRow pendingRow, uint? groundItemId, CancellationToken cancellationToken)
    {
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Drop,
            _ => [new RemoveFromRowMutation(InventoryContainer.PendingGroundDrop, pendingRow.DurableId, pendingRow.Amount, pendingRow.Amount, pendingRow.Revision)],
            cancellationToken, operationId: Settlement(record.DropId, 1), resolvesPendingDrop: record.DropId);
        // Conflict: another settler (RecoverAsync on another MapServer) already settled it, necessarily with the same decision.
        return change.Succeeded || change.Status == InventoryChangeStatus.Conflict ? (DropResult.Dropped, groundItemId) : (DropResult.Pending, groundItemId);
    }

    private async Task<(DropResult, uint?)> ReturnAsync(PendingGroundDrop record, InventoryItemRow pendingRow, IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken)
    {
        var restored = pendingRow with { DurableId = 0, Revision = 0 };
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Drop,
            _ => [new RemoveFromRowMutation(InventoryContainer.PendingGroundDrop, pendingRow.DurableId, pendingRow.Amount, pendingRow.Amount, pendingRow.Revision),
                  new AddNewRowMutation(InventoryContainer.Inventory, restored)],
            cancellationToken, operationId: Settlement(record.DropId, 2), resolvesPendingDrop: record.DropId);
        return change.Succeeded || change.Status == InventoryChangeStatus.Conflict ? (DropResult.Returned, null) : (DropResult.Pending, null);
    }

    // pc_dropitem's own refusals, in pinned order, then pc_candrop for a group-0 player.
    private static DropResult Check(CharacterInventoryItem? row, uint amount, IReadOnlyDictionary<int, ItemDefinition> items)
    {
        if (row is null || !items.TryGetValue(row.ItemId, out var definition)) return DropResult.UnknownRow;
        if (amount == 0 || amount > row.Amount) return DropResult.InvalidAmount;
        // Divergence: pinned pc_delitem would unequip a worn row; the stock client never offers it, so it is refused here.
        if (row.Equip != 0) return DropResult.Equipped;
        if (row.Details.EquipSwitch != 0) return DropResult.RegisteredForEquipSwitch;
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

    // Settlement operation ids are derived from the drop id, so every settler of one drop sends the same id (CharServer replays
    // instead of applying twice) and the two kinds of settlement never share one.
    private static Guid Settlement(Guid dropId, byte kind)
    {
        var bytes = dropId.ToByteArray();
        bytes[15] ^= (byte)(0xA0 | kind);
        return new Guid(bytes);
    }
}
