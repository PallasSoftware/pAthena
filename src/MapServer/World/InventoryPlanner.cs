using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Turns "give / take items" into the durable mutations CharServer applies (CharServer never decides stacking). Pure functions of the
// current snapshot, so a plan is exactly as stale as the revisions it names - CharServer rejects it if a row moved.
// This is mechanics only (which rows merge, split and delete); WHETHER a grant is allowed (capacity, weight, restrictions) is decided by the
// inventory rules before a plan is made.
public static class InventoryPlanner
{
    /// <summary>Pinned MAX_AMOUNT (mmo.hpp): the largest stack of any item unless the item declares a smaller Stack.Amount.</summary>
    public const uint MaxAmount = 30_000;

    public static uint StackLimit(ItemDefinition definition)
    {
        var data = definition.Meta?.Data;
        return data is { StackAmount: > 0 } && data.Flags.HasFlag(PackItemFlags.StackInventory) ? Math.Min(data.StackAmount, MaxAmount) : MaxAmount;
    }

    /// <summary>
    /// Plans granting several items at once (a container's contents, a quest reward). Stackable items extend an existing mergeable stack
    /// before opening a new row; non-stackable items get one row each. Returns null when there is nothing to grant or the operation would
    /// need more than one wire operation can carry.
    /// </summary>
    public static IReadOnlyList<InventoryMutation>? PlanGrants(CharacterInventorySnapshot inventory, IEnumerable<(ItemDefinition Definition, uint Amount)> grants)
    {
        // Targets in first-touched order, so the resulting mutations (and the client's pickup notifications) follow the order the grants were given.
        var targets = new List<GrantTarget>();

        foreach (var (definition, requested) in grants)
        {
            if (requested == 0) return null;
            if (!definition.Stackable)
            {
                for (var i = 0; i < requested; i++) targets.Add(new GrantTarget(definition, null) { Amount = 1 });
                continue;
            }

            var limit = StackLimit(definition);
            var remaining = requested;
            foreach (var row in inventory.Items.Where(row => CanMerge(row, definition)).OrderBy(row => row.DurableId))
            {
                if (remaining == 0) break;
                var target = targets.FirstOrDefault(t => t.Existing?.DurableId == row.DurableId) ?? Add(targets, new GrantTarget(definition, row));
                if (row.Amount + target.Amount >= limit) continue;
                var add = Math.Min(remaining, limit - row.Amount - target.Amount);
                target.Amount += add;
                remaining -= add;
            }

            foreach (var target in targets.Where(t => t.Existing is null && t.Definition.Id == definition.Id && t.Amount < limit))
            {
                if (remaining == 0) break;
                var add = Math.Min(remaining, limit - target.Amount);
                target.Amount += add;
                remaining -= add;
            }

            while (remaining > 0)
            {
                var amount = Math.Min(remaining, limit);
                targets.Add(new GrantTarget(definition, null) { Amount = amount });
                remaining -= amount;
            }
        }

        var mutations = new List<InventoryMutation>();
        foreach (var target in targets.Where(t => t.Amount > 0))
        {
            mutations.Add(target.Existing is { } row
                ? new AddToStackMutation(InventoryContainer.Inventory, row.DurableId, (uint)row.ItemId, target.Amount, row.Amount, MaxAmount, row.Details.Revision)
                : new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New((uint)target.Definition.Id, target.Amount)));
        }

        return mutations.Count is 0 or > InventoryOperationWire.MaxMutations ? null : mutations;
    }

    private sealed class GrantTarget(ItemDefinition definition, CharacterInventoryItem? existing)
    {
        public ItemDefinition Definition { get; } = definition;
        public CharacterInventoryItem? Existing { get; } = existing;
        public uint Amount { get; set; }
    }

    private static GrantTarget Add(List<GrantTarget> targets, GrantTarget target) { targets.Add(target); return target; }

    /// <summary>
    /// Plans removing <paramref name="amount"/> of an item id across its rows in ascending durable id order (pc_delitem scanning the array);
    /// null when the character does not own enough. Equipped rows are never consumed implicitly.
    /// </summary>
    public static IReadOnlyList<InventoryMutation>? PlanRemoveByItemId(CharacterInventorySnapshot inventory, int itemId, uint amount)
    {
        if (amount == 0) return null;
        var rows = inventory.Items.Where(row => row.ItemId == itemId && row.Equip == 0).OrderBy(row => row.DurableId).ToArray();
        if (rows.Sum(row => (long)row.Amount) < amount) return null;

        var mutations = new List<InventoryMutation>();
        var remaining = amount;
        foreach (var row in rows)
        {
            if (remaining == 0) break;
            var take = Math.Min(remaining, row.Amount);
            mutations.Add(new RemoveFromRowMutation(InventoryContainer.Inventory, row.DurableId, take, row.Amount, row.Details.Revision));
            remaining -= take;
        }
        return mutations.Count > InventoryOperationWire.MaxMutations ? null : mutations;
    }

    public static RemoveFromRowMutation RemoveFromRow(CharacterInventoryItem row, uint amount) =>
        new(InventoryContainer.Inventory, row.DurableId, amount, row.Amount, row.Details.Revision);

    // Stackable rows merge only when everything that distinguishes two stacks matches (pc_additem: nameid, bound, unique id, expiry,
    // cards). Equipped rows never merge; a rental (expire_time) never stacks.
    private static bool CanMerge(CharacterInventoryItem row, ItemDefinition definition) =>
        row.ItemId == definition.Id && row.Equip == 0 && row.Bound == 0 && row.Details is { ExpireTime: 0, UniqueId: 0, Card0: 0, Card1: 0, Card2: 0, Card3: 0 };
}
