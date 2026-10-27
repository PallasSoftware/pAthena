using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned storage_additem (storage.cpp:241-296) / storage_canAddItem: generic across Storage and
// Cart (both are plain MAX_AMOUNT-capped slot arrays with a per-item Stack.Amount cap, no weight
// concept for storage - cart's own weight cap is a documented later extension, see ContainerLimits'
// own doc comment). Deliberately NOT the same type as InventoryPlanner.PlanGrants: that method is
// the player's own carried inventory (weight limits, favorite/equip-slot concepts, runtime
// SlotIndex view) - storage/cart are a plain durable row list with none of that, so reusing
// InventoryPlanner's own IInventoryRules/CharacterInventorySnapshot types here would force an
// equip-slot/weight model onto a container that pinned source never gives one.
public static class ContainerPlanner
{
    // Default MAX_STORAGE / MAX_CART (mmo.hpp:91,97) - a purchased/upgraded storage size is a
    // later extension (no persistence column for it exists yet); this is the pinned maximum, used
    // as the simple fixed cap until that exists.
    public const int MaxStorageSlots = 600;
    public const int MaxCartSlots = 100;

    public readonly record struct TransferPlan(bool Succeeded, IReadOnlyList<InventoryMutation>? Mutations, StorageTransferResult Result);

    // Mirrors AddItemResult's shape for the two outcomes storage_additem actually reports (pinned
    // return 1 = "not storable"/generic reject, return 2 = stack-limit-or-full) - a much smaller
    // set than the player inventory's own AddItemResult, since storage/cart have no weight/
    // overweight concept at all.
    public enum StorageTransferResult : byte { Success, NotStorable, StackLimit, Full, Invalid }

    // One durable operation: remove `amount` of the named inventory row AND add the same amount
    // into `targetContainer` (Storage or Cart) - NEVER two separate CharServer round trips (pinned
    // storage_storageadd's own add-then-delete sequence is not atomic in real rAthena; this project
    // does strictly better by making it one operation). `targetContainer` rows are passed as plain
    // InventoryItemRow (no CharacterInventoryItem/SlotIndex concept - storage/cart have no runtime
    // "hole" semantics requirement, and client projection is deferred - see this file's own doc
    // comment).
    public static TransferPlan PlanTransferIn(
        CharacterInventoryItem sourceRow, uint amount, IReadOnlyList<InventoryItemRow> targetRows, int maxSlots,
        IReadOnlyDictionary<int, ItemDefinition> items, InventoryContainer targetContainer)
    {
        if (amount == 0 || amount > sourceRow.Amount) return new(false, null, StorageTransferResult.Invalid);
        if (sourceRow.Equip != 0) return new(false, null, StorageTransferResult.NotStorable); // pc_candrop-equivalent: an equipped row is never transferable.
        // Pinned clif_parse_MoveToKafra (clif.cpp:13634-13637) / pc_putitemtocart (pc.cpp:6699-6702): a row registered in the
        // equip-switch set must be unregistered first (MSI_SWAP_EQUIPITEM_UNREGISTER_FIRST).
        if (sourceRow.Details.EquipSwitch != 0) return new(false, null, StorageTransferResult.NotStorable);
        if (!items.TryGetValue(sourceRow.ItemId, out var item)) return new(false, null, StorageTransferResult.Invalid);
        // Pinned itemdb_cancartstore_sub/itemdb_canstore_sub (itemdb.cpp:3294-3299): NoCart/NoStorage block the
        // transfer unless a GM's trade-override level clears it - this project has no GM-permission/trade-override
        // concept yet, so the restriction is
        // enforced unconditionally until that concept exists, never silently ignored.
        if (IsRestricted(item, targetContainer)) return new(false, null, StorageTransferResult.NotStorable);

        var stackCap = StackCapFor(item, targetContainer);
        if (stackCap is { } cap && amount > cap) return new(false, null, StorageTransferResult.StackLimit);

        var mutations = new List<InventoryMutation> { new RemoveFromRowMutation(InventoryContainer.Inventory, sourceRow.DurableId, amount, sourceRow.Amount, sourceRow.Details.Revision) };

        if (item.Stackable)
        {
            var existing = targetRows.FirstOrDefault(row => MatchesForStacking(row, sourceRow));
            if (existing is not null)
            {
                var newAmount = (long)existing.Amount + amount;
                if (newAmount > uint.MaxValue || (stackCap is { } c2 && newAmount > c2)) return new(false, null, StorageTransferResult.StackLimit);
                mutations.Add(new AddToStackMutation(targetContainer, existing.DurableId, existing.ItemId, amount, existing.Amount, stackCap ?? uint.MaxValue, existing.Revision));
                return new(true, mutations, StorageTransferResult.Success);
            }
        }

        if (targetRows.Count >= maxSlots) return new(false, null, StorageTransferResult.Full);
        var newRow = sourceRow.ToRow() with { DurableId = 0, Revision = 0, Amount = amount };
        mutations.Add(new AddNewRowMutation(targetContainer, newRow));
        return new(true, mutations, StorageTransferResult.Success);
    }

    // The reverse direction: remove `amount` from a container row and add it into the inventory.
    // Inventory-side capacity (weight/slot limits) is the CALLER's own responsibility via the
    // normal InventoryPlanner.PlanGrants path - this method only plans the CONTAINER-side removal,
    // since the two sides use genuinely different capacity rules (see this file's own doc comment).
    public static RemoveFromRowMutation PlanTransferOutRemoval(InventoryItemRow containerRow, uint amount, InventoryContainer sourceContainer)
    {
        if (amount == 0 || amount > containerRow.Amount) throw new ArgumentOutOfRangeException(nameof(amount));
        return new RemoveFromRowMutation(sourceContainer, containerRow.DurableId, amount, containerRow.Amount, containerRow.Revision);
    }

    // Pinned compare_item (storage.cpp): same nameid, identify, refine, attribute, cards/options -
    // never merges two otherwise-identical rows that differ in any of these. Bound/expire/unique
    // rows are never stackable at all (itemdb_isstackable2), so a caller only reaches this check
    // for a genuinely stackable item definition already.
    private static bool MatchesForStacking(InventoryItemRow row, CharacterInventoryItem source) =>
        row.ItemId == (uint)source.ItemId && row.Identify == (source.Identified ? 1 : 0) && row.Refine == source.Refine &&
        row.Attribute == source.Details.Attribute && row.Card0 == source.Details.Card0 && row.Card1 == source.Details.Card1 &&
        row.Card2 == source.Details.Card2 && row.Card3 == source.Details.Card3;

    private static bool IsRestricted(ItemDefinition item, InventoryContainer container)
    {
        var data = item.Meta?.Data;
        if (data is null) return false;
        return container switch
        {
            InventoryContainer.Storage => data.Flags.HasFlag(PackItemFlags.NoStorage),
            InventoryContainer.Cart => data.Flags.HasFlag(PackItemFlags.NoCart),
            InventoryContainer.GuildStorage => data.Flags.HasFlag(PackItemFlags.NoGuildStorage),
            _ => false,
        };
    }

    private static uint? StackCapFor(ItemDefinition item, InventoryContainer container)
    {
        var data = item.Meta?.Data;
        if (data is null) return null;
        var flagged = container switch
        {
            InventoryContainer.Storage => data.Flags.HasFlag(PackItemFlags.StackStorage),
            InventoryContainer.Cart => data.Flags.HasFlag(PackItemFlags.StackCart),
            InventoryContainer.GuildStorage => data.Flags.HasFlag(PackItemFlags.StackGuildStorage),
            _ => false,
        };
        return flagged && data.StackAmount > 0 ? data.StackAmount : null;
    }
}
