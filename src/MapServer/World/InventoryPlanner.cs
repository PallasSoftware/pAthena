using Athena.Net.MapServer.Gameplay.Rules;
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
    /// <summary>One thing to put into the inventory. <see cref="Stacked"/> false gives stackable items one row per unit (item group Stacked: false).</summary>
    public sealed record GrantRequest(ItemDefinition Item, uint Amount, InventoryItemRow? Template = null, bool Stacked = true);

    /// <summary>Outcome of planning several grants: the mutations, or the pinned pc_additem failure of the first grant that does not fit.</summary>
    public sealed record GrantPlan(AddItemResult Result, IReadOnlyList<InventoryMutation>? Mutations, int FailedGrant)
    {
        public bool Succeeded => Result == AddItemResult.Success;
    }

    private sealed class Target
    {
        public CharacterInventoryItem? Existing;
        public InventoryItemRow? New;
        public uint Added;
    }

    /// <summary>
    /// Plans granting several items as ONE all-or-nothing operation by running the regional pc_additem rules for each unit in order against a
    /// simulated copy of the inventory (so capacity, weight and merging see the earlier grants of the same operation). Stackable items extend the
    /// first matching stack or open a new row; non-stackable items get one row per unit. The first grant that does not fit fails the whole plan.
    /// </summary>
    public static GrantPlan PlanGrants(
        IInventoryRules rules, CharacterInventorySnapshot inventory, InventoryLimits limits, IReadOnlyDictionary<int, ItemDefinition> items, IEnumerable<GrantRequest> grants)
    {
        var simulated = inventory;
        var weight = rules.TotalWeight(inventory, items);
        var targets = new List<Target>();
        uint temporaryId = uint.MaxValue;
        var index = 0;

        foreach (var grant in grants)
        {
            // pc_additem takes one amount; callers split non-stackable (or unstacked) grants into single units.
            var units = grant.Item.Stackable && grant.Stacked ? [grant.Amount] : Enumerable.Repeat(1u, (int)Math.Min(grant.Amount, InventoryOperationWire.MaxMutations + 1u)).ToArray();
            if (grant.Amount == 0) return new(AddItemResult.Invalid, null, index);

            foreach (var unit in units)
            {
                var template = grant.Template;
                var candidate = new AddItemCandidate(grant.Item, unit, template?.Bound ?? 0, template?.ExpireTime ?? 0, template?.UniqueId ?? 0,
                    template?.Card0 ?? 0, template?.Card1 ?? 0, template?.Card2 ?? 0, template?.Card3 ?? 0);
                var decision = rules.EvaluateAdd(simulated, limits, weight, candidate);
                if (!decision.Succeeded) return new(decision.Result, null, index);

                weight += decision.AddedWeight;
                if (decision.MergeInto is { } row)
                {
                    var target = targets.FirstOrDefault(t => t.Existing?.DurableId == row.DurableId) ?? Add(targets, new Target { Existing = row });
                    target.Added += unit;
                    simulated = simulated.WithUpdatedItem(row.DurableId, row.ItemId, row.Amount + unit, row.Equip, row.Identified, row.Refine, row.Favorite, row.Bound);
                }
                else
                {
                    var created = (template ?? InventoryItemRow.New((uint)grant.Item.Id, unit, rules.IsCreatedIdentified(grant.Item) ? (byte)1 : (byte)0))
                        with { ItemId = (uint)grant.Item.Id, Amount = unit, DurableId = 0, Revision = 0 };
                    if (candidate.UniqueId == 0 && grant.Item.Meta?.Data.Flags.HasFlag(PackItemFlags.UniqueId) == true) created = created with { UniqueId = InventoryItemRow.GenerateUniqueId };
                    Add(targets, new Target { New = created, Added = unit });
                    simulated = new CharacterInventorySnapshot([.. simulated.Items, CharacterInventoryItem.FromRow(created with { DurableId = temporaryId-- }, decision.NewSlot!.Value)]);
                }
            }
            index++;
        }

        var mutations = new List<InventoryMutation>();
        foreach (var target in targets)
        {
            mutations.Add(target.Existing is { } existing
                ? new AddToStackMutation(InventoryContainer.Inventory, existing.DurableId, (uint)existing.ItemId, target.Added, existing.Amount, StackCap(items, existing.ItemId), existing.Details.Revision)
                : new AddNewRowMutation(InventoryContainer.Inventory, target.New!));
        }

        return mutations.Count is 0 or > InventoryOperationWire.MaxMutations
            ? new(mutations.Count == 0 ? AddItemResult.Invalid : AddItemResult.OverItem, null, -1)
            : new(AddItemResult.Success, mutations, -1);
    }

    // CharServer re-checks the cap on the row it updates (defense in depth): Stack.Amount when the item declares one for the inventory, else MAX_AMOUNT.
    private static uint StackCap(IReadOnlyDictionary<int, ItemDefinition> items, int itemId) =>
        items.TryGetValue(itemId, out var definition) && definition.Meta?.Data is { StackAmount: > 0 } data && data.Flags.HasFlag(PackItemFlags.StackInventory) ? Math.Min(data.StackAmount, 30_000u) : 30_000u;

    private static Target Add(List<Target> targets, Target target) { targets.Add(target); return target; }

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

}
