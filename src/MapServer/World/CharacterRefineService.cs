using Athena.Net.MapServer.Generated.GameData.Refine;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned clif_parse_refineui_refine (clif.cpp:22644-22810): attempts to refine one equipment row by one level,
// consuming a chosen material (and optional blacksmith blessings, not modeled here - no pinned item carries a
// nonzero BlacksmithBlessingAmount gap this project's own test fixtures have exercised yet) and zeny, then
// rolling success/failure against the real pinned Chances[] table. Only the server-authoritative mutation is
// modeled: the client Refine UI packets (0xAA0-0xAA3) have no capture evidence yet (ai/iro-2026-wire.md), matching
// the exact deferral already applied to repair/identify-list/card-insert/storage-open. This gives that eventual
// wire-protocol caller (or an NPC-script successrefitem-style trigger, though that builtin itself stays Feature
// 20's own territory per the Feature 16 ownership table) a durable, idempotent, fully-tested operation to invoke.
//
// zeny: MapServer has no runtime zeny-balance tracking of its own yet (see InventoryOperationRequest's own doc
// comment on ZenyCost/ExpectedZeny) - the caller supplies expectedZeny itself, exactly like it already supplies
// every other ExpectedRevision/ExpectedAmount precondition elsewhere in this domain model. CharServer is the one
// source of truth that actually validates and charges it. The actual cost is computed INSIDE
// CharacterInventoryService's own zeny-aware ExecuteAsync overload, against the same live snapshot the row
// mutations are planned from - never precomputed outside the gate, which would risk planning against one
// snapshot and charging for another.
public sealed class CharacterRefineService(CharacterInventoryService inventoryService, Func<int>? rollSource = null)
{
    public enum RefineAttemptResult
    {
        Success, Failure, Broken, Downgraded,
        UnknownRow, NotRefineable, MaxRefine, InsufficientMaterial, InsufficientZeny, PersistenceFailed,
    }

    private readonly Func<int> _roll = rollSource ?? (() => System.Random.Shared.Next(0, 10000));

    // costType: the pinned Chances[].Type this attempt pays with ("Normal"/"HD"/"Enriched" - see
    // RefineDataCompiler.CostTypes).
    public async Task<(RefineAttemptResult Result, CharacterInventorySnapshot? Updated)> RefineAsync(
        uint targetDurableId, string costType, uint expectedZeny, IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken)
    {
        var decided = RefineAttemptResult.UnknownRow;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Refine, inventory =>
        {
            var cost = TryResolveCost(inventory, targetDurableId, costType, items, out decided);
            if (cost is null) return (null, 0u);
            if (cost.Value.Price > expectedZeny) { decided = RefineAttemptResult.InsufficientZeny; return (null, 0u); }

            var target = inventory.Items.First(i => i.DurableId == targetDurableId);
            var materialMutations = InventoryPlanner.PlanRemoveByItemId(inventory, cost.Value.MaterialItemId, 1);
            if (materialMutations is null) { decided = RefineAttemptResult.InsufficientMaterial; return (null, 0u); }

            IReadOnlyList<InventoryMutation> result;
            // Pinned: `if (cost->chance >= (rnd() % 10000))` - success (clif.cpp:22752).
            if (cost.Value.Rate >= _roll())
            {
                decided = RefineAttemptResult.Success;
                result = [.. materialMutations, new UpdateRowMutation(InventoryContainer.Inventory, target.DurableId, target.Details.Revision, target.ToRow() with { Refine = (byte)(target.Refine + 1) })];
            }
            // Pinned failure branch order (clif.cpp:22776-22800): breaking roll first, then downgrade, else a
            // plain failure - zeny and material are already spent at this point either way (clif.cpp:22746-22751).
            else if (cost.Value.BreakingRate > 0 && cost.Value.BreakingRate > _roll())
            {
                decided = RefineAttemptResult.Broken;
                result = [.. materialMutations, InventoryPlanner.RemoveFromRow(target, target.Amount)];
            }
            else if (cost.Value.DowngradeAmount > 0)
            {
                decided = RefineAttemptResult.Downgraded;
                // target.Refine (byte) promotes to uint against cost.Value.DowngradeAmount (uint) - an unsigned
                // subtraction that would UNDERFLOW (wrap to a huge value, not go negative) if DowngradeAmount
                // ever exceeded the current refine level. Cast to int first so Math.Max(0, ...) actually clamps.
                var downgraded = (byte)Math.Max(0, (int)target.Refine - (int)cost.Value.DowngradeAmount);
                result = [.. materialMutations, new UpdateRowMutation(InventoryContainer.Inventory, target.DurableId, target.Details.Revision, target.ToRow() with { Refine = downgraded })];
            }
            else
            {
                decided = RefineAttemptResult.Failure;
                result = [.. materialMutations];
            }
            return (result, cost.Value.Price);
        }, cancellationToken, expectedZeny: expectedZeny);

        if (!IsAttempted(decided)) return (decided, null);
        return change.Succeeded ? (decided, change.Snapshot) : (RefineAttemptResult.PersistenceFailed, null);
    }

    // Resolves the target row/definition/next-refine-level cost against the GIVEN snapshot, matching pinned
    // refine_db.findLevelInfo's own "look up the level this attempt is TRYING to reach" (clif.cpp:22688-22693,
    // the item's CURRENT refine+1, never the current level itself).
    private static (uint Rate, uint Price, int MaterialItemId, uint BreakingRate, uint DowngradeAmount)? TryResolveCost(
        CharacterInventorySnapshot inventory, uint targetDurableId, string costType, IReadOnlyDictionary<int, ItemDefinition> items, out RefineAttemptResult result)
    {
        result = RefineAttemptResult.UnknownRow;
        var target = inventory.Items.FirstOrDefault(i => i.DurableId == targetDurableId);
        if (target is null) return null;
        if (!items.TryGetValue(target.ItemId, out var definition)) return null;
        if (definition.Meta is not { Refineable: true } meta) { result = RefineAttemptResult.NotRefineable; return null; }
        var nextRefine = target.Refine + 1;

        // Only Weapon and Armor are tabulated at all (RefineDataCompiler's own scope - shadow gear/ammo/etc. are
        // never emitted); anything else with Refineable somehow set is rejected explicitly rather than silently
        // misclassified into whichever of the two groups happens to come first.
        var (group, itemLevel) = definition switch
        {
            WeaponItemDefinition weapon => ("Weapon", weapon.WeaponLevel),
            ArmorItemDefinition => ("Armor", (int)meta.ArmorLevel),
            _ => (null, 0),
        };
        if (group is null) { result = RefineAttemptResult.NotRefineable; return null; }

        var cost = GeneratedRefineBonus.Cost(group, itemLevel, nextRefine, costType);
        if (cost is null) { result = RefineAttemptResult.MaxRefine; return null; } // no further refine possible at this level/cost-type - matches pinned null for "no Chances entry".
        return cost;
    }

    private static bool IsAttempted(RefineAttemptResult result) => result is RefineAttemptResult.Success or RefineAttemptResult.Failure or RefineAttemptResult.Broken or RefineAttemptResult.Downgraded;
}
