using Athena.Net.MapServer.Generated.GameData.Refine;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned clif_parse_enchantgrade_start (clif.cpp:24059-24243): attempts to raise one equipment row's enchant grade
// by one step. Mirrors CharacterRefineService's own structure closely (same atomic zeny+inventory transaction
// primitive, same "material/zeny spent either way" ordering) but has its own distinct mutation shape:
//   - The success-roll CHANCE is keyed by the item's CURRENT REFINE LEVEL (not the target grade), optionally
//     raised by a catalyst/"blessing" item consumed in steps (clif.cpp:24104-24172).
//   - On success: grade+1 (capped at the pinned MaxEnchantGrade), AND the item's refine RESETS TO 0
//     (clif.cpp:24202-24205 - "On successful enchantgrade increase the refine is reset", not preserved).
//   - On failure: breaking roll first (deletes the whole item), else downgrade (reduces REFINE by
//     DowngradeAmount, clamped >=0 - the grade itself never changes on failure), else a plain failure.
// Only the server-authoritative mutation is modeled: the client enchant-grade UI packets have no capture evidence
// yet (ai/iro-2026-wire.md), matching the exact deferral already applied to refine/repair/identify-list/card-insert.
public sealed class CharacterEnchantGradeService(CharacterInventoryService inventoryService, Func<int>? rollSource = null)
{
    public enum EnchantGradeAttemptResult
    {
        Success, Failure, Broken, Downgraded,
        UnknownRow, NotGradable, MaxGrade, UnknownOption, InsufficientMaterial, InsufficientCatalyst, InsufficientZeny, PersistenceFailed,
    }

    private readonly Func<int> _roll = rollSource ?? (() => System.Random.Shared.Next(0, 10000));

    // optionIndex: the pinned client Option index (clif_parse_enchantgrade_start's own p->material_index) -
    // selects which of this grade's tabulated Options[] cost tier to pay. catalystSteps: the pinned
    // p->blessing_amount (0 if the attempt uses no catalyst at all), clamped to the tabulated MaximumSteps the
    // same way the pinned `min(p->blessing_amount, enchantgradelevel->catalyst.maximumSteps)` does.
    public async Task<(EnchantGradeAttemptResult Result, CharacterInventorySnapshot? Updated)> UpgradeAsync(
        uint targetDurableId, int optionIndex, uint catalystSteps, uint expectedZeny, IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken)
    {
        var decided = EnchantGradeAttemptResult.UnknownRow;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.EnchantGrade, inventory =>
        {
            var resolved = TryResolveAttempt(inventory, targetDurableId, optionIndex, catalystSteps, items, out decided);
            if (resolved is null) return (null, 0u);
            var (target, group, itemLevel, gradeName, option, totalChance, steps) = resolved.Value;
            if (option.Zeny > expectedZeny) { decided = EnchantGradeAttemptResult.InsufficientZeny; return (null, 0u); }

            // Pinned clif.cpp:24126-24186: the catalyst and option material, SUMMED when they are the SAME item
            // (never two separate partial deletions of one item id), otherwise checked/consumed independently.
            var materialMutations = BuildMaterialMutations(inventory, group, itemLevel, gradeName, option, steps, out var materialOk);
            if (!materialOk) { decided = EnchantGradeAttemptResult.InsufficientMaterial; return (null, 0u); }

            IReadOnlyList<InventoryMutation> result;
            // Pinned: `if (rnd()%10000 < totalChance)` - success (clif.cpp:24199).
            if (_roll() < totalChance)
            {
                decided = EnchantGradeAttemptResult.Success;
                var nextGrade = (byte)Math.Min(target.Details.EnchantGrade + 1, GeneratedEnchantGradeBonus.Grades.Length - 1);
                result = [.. materialMutations, new UpdateRowMutation(InventoryContainer.Inventory, target.DurableId, target.Details.Revision,
                    target.ToRow() with { Refine = 0, EnchantGrade = nextGrade })];
            }
            // Pinned failure branch order (clif.cpp:24221-24240): breaking roll first, then downgrade, else a
            // plain failure - the catalyst/option material and zeny are already spent at this point either way.
            else if (option.BreakingRate > 0 && _roll() < option.BreakingRate)
            {
                decided = EnchantGradeAttemptResult.Broken;
                result = [.. materialMutations, InventoryPlanner.RemoveFromRow(target, target.Amount)];
            }
            else if (option.DowngradeAmount > 0)
            {
                decided = EnchantGradeAttemptResult.Downgraded;
                // See CharacterRefineService's own identical comment: cast to int first so Math.Max(0, ...)
                // actually clamps instead of wrapping on an unsigned underflow.
                var downgraded = (byte)Math.Max(0, (int)target.Refine - (int)option.DowngradeAmount);
                result = [.. materialMutations, new UpdateRowMutation(InventoryContainer.Inventory, target.DurableId, target.Details.Revision, target.ToRow() with { Refine = downgraded })];
            }
            else
            {
                decided = EnchantGradeAttemptResult.Failure;
                result = [.. materialMutations];
            }
            return (result, option.Zeny);
        }, cancellationToken, expectedZeny: expectedZeny);

        if (!IsAttempted(decided)) return (decided, null);
        return change.Succeeded ? (decided, change.Snapshot) : (EnchantGradeAttemptResult.PersistenceFailed, null);
    }

    private static (CharacterInventoryItem Target, string Group, int ItemLevel, string GradeName, GeneratedEnchantGradeBonus.Option Option, uint TotalChance, uint Steps)?
        TryResolveAttempt(CharacterInventorySnapshot inventory, uint targetDurableId, int optionIndex, uint catalystSteps,
            IReadOnlyDictionary<int, ItemDefinition> items, out EnchantGradeAttemptResult result)
    {
        result = EnchantGradeAttemptResult.UnknownRow;
        var target = inventory.Items.FirstOrDefault(i => i.DurableId == targetDurableId);
        if (target is null) return null;
        if (!items.TryGetValue(target.ItemId, out var definition)) return null;
        if (definition.Meta is not { Gradable: true } meta) { result = EnchantGradeAttemptResult.NotGradable; return null; }

        var (group, itemLevel) = definition switch
        {
            WeaponItemDefinition weapon => ("Weapon", weapon.WeaponLevel),
            ArmorItemDefinition => ("Armor", (int)meta.ArmorLevel),
            _ => (null, 0),
        };
        if (group is null) { result = EnchantGradeAttemptResult.NotGradable; return null; }

        if (target.Details.EnchantGrade >= GeneratedEnchantGradeBonus.Grades.Length - 1) { result = EnchantGradeAttemptResult.MaxGrade; return null; }
        var gradeName = GeneratedEnchantGradeBonus.Grades[target.Details.EnchantGrade];

        var totalChance = GeneratedEnchantGradeBonus.ChanceAtRefine(group, itemLevel, gradeName, target.Refine);
        if (totalChance == 0) { result = EnchantGradeAttemptResult.MaxGrade; return null; } // pinned "No chance to increase" (clif.cpp:24106-24109).

        var option = GeneratedEnchantGradeBonus.OptionCost(group, itemLevel, gradeName, optionIndex);
        if (option is null) { result = EnchantGradeAttemptResult.UnknownOption; return null; }

        var catalyst = GeneratedEnchantGradeBonus.CatalystFor(group, itemLevel, gradeName);
        var steps = catalyst is null ? 0u : Math.Min(catalystSteps, catalyst.Value.MaximumSteps);
        if (steps > 0) totalChance += steps * catalyst!.Value.ChanceIncrease;

        return (target, group, itemLevel, gradeName, option.Value, totalChance, steps);
    }

    // Pinned clif.cpp:24126-24186. Returns InventoryPlanner-produced mutations for whichever material rows this
    // attempt consumes; materialOk is false exactly when the pinned function would have returned early for an
    // insufficient amount (no mutation planned in that case).
    private static IReadOnlyList<InventoryMutation> BuildMaterialMutations(
        CharacterInventorySnapshot inventory, string group, int itemLevel, string gradeName, GeneratedEnchantGradeBonus.Option option, uint steps, out bool materialOk)
    {
        materialOk = false;
        var catalyst = GeneratedEnchantGradeBonus.CatalystFor(group, itemLevel, gradeName);
        if (steps == 0 || catalyst is null)
        {
            // No catalyst used this attempt: only the option's own material is consumed.
            var optionOnly = InventoryPlanner.PlanRemoveByItemId(inventory, option.MaterialItemId, option.Amount);
            if (optionOnly is null) return [];
            materialOk = true;
            return optionOnly;
        }

        if (catalyst.Value.MaterialItemId == option.MaterialItemId)
        {
            // Same item as the option's own material: ONE combined deletion of the summed amount (pinned
            // clif.cpp:24128-24141), never two separate partial deletions of the same row.
            var combinedAmount = catalyst.Value.AmountPerStep * steps + option.Amount;
            var combined = InventoryPlanner.PlanRemoveByItemId(inventory, option.MaterialItemId, combinedAmount);
            if (combined is null) return [];
            materialOk = true;
            return combined;
        }

        // Distinct catalyst and option materials: each is checked/consumed independently (pinned clif.cpp:24143-24169).
        var catalystMutations = InventoryPlanner.PlanRemoveByItemId(inventory, catalyst.Value.MaterialItemId, catalyst.Value.AmountPerStep * steps);
        if (catalystMutations is null) return [];
        var optionMutations = InventoryPlanner.PlanRemoveByItemId(inventory, option.MaterialItemId, option.Amount);
        if (optionMutations is null) return [];
        materialOk = true;
        return [.. catalystMutations, .. optionMutations];
    }

    private static bool IsAttempted(EnchantGradeAttemptResult result) =>
        result is EnchantGradeAttemptResult.Success or EnchantGradeAttemptResult.Failure or EnchantGradeAttemptResult.Broken or EnchantGradeAttemptResult.Downgraded;
}
