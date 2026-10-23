using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Tests.World.Items;

// S7 coverage audit (data-driven acceptance requirement, docs/gameplay/feature-16-execution-plan.md): proves every
// script EquipmentBonusResolver can actually be asked to run - every Weapon/Armor/Card item's own Script, every
// item_randomopt_db.yml entry, every item_combos.yml entry's shared Script - is classified into exactly one outcome,
// never silently "parsed but untested". Runs against the REAL committed ItemCatalog.Current (the production
// conversion pipeline's own output), not a hand-built fixture, so a pack regeneration that breaks coverage fails
// this test immediately.
//
// Scope note: this is deliberately NARROWER than "every script in the pack" (21k+ distinct scripts across the whole
// item_db, including Usable/Healing/consumable on-use scripts) - those are not equipment-bonus scripts at all and
// including them in a "resolver coverage" percentage would be misleading (most legitimately fail against a resolver
// built only for equip-time bonus accumulation). Only scripts TryResolveEquipment can actually reach are counted.
public sealed class EquipmentBonusCoverageTests
{
    private enum Outcome { Resolved, UnsupportedConstruct, Deferred }

    private readonly record struct Classified(Outcome Outcome, string? Detail);

    private static Classified Classify(PackScript? script, InventoryOption option = default)
    {
        if (script is null) return new(Outcome.Resolved, null); // no script at all is zero bonus, not a gap.
        if (script.Support == PackScriptSupport.Deferred) return new(Outcome.Deferred, $"{script.DeferredOwner}:{script.DeferredConstruct}");
        try { EquipmentBonusResolver.Resolve(script, 0, option); return new(Outcome.Resolved, null); }
        catch (EquipmentBonusResolver.UnsupportedEquipScriptException ex) { return new(Outcome.UnsupportedConstruct, ex.Message); }
    }

    // Every Weapon/Armor/Card item's own Script (the field TryResolveEquipment actually reads - see
    // EquipmentBonusResolver's own doc comment on why Script, not EquipScript/UnEquipScript). Ammo/ShadowGear/
    // PetEgg/etc. are explicitly out of scope: TryResolveEquipment never calls EquipmentBonusResolver for them.
    private static IEnumerable<(int ItemId, string AegisName, PackScript? Script)> EligibleItemScripts(ItemCatalog catalog) =>
        catalog.ById.Values
            .Where(def => def is WeaponItemDefinition or ArmorItemDefinition or CardItemDefinition)
            .Select(def => (def.Id, def.AegisName, def.Meta?.Script));

    [Fact]
    public void Coverage_EveryEligibleItemScript_IsClassified_NoneThrowAnUnhandledException()
    {
        var catalog = ItemCatalog.Current;
        var total = 0;
        foreach (var (itemId, aegisName, script) in EligibleItemScripts(catalog))
        {
            total++;
            // Classify must never itself throw anything other than the resolver's own typed exception (already
            // caught inside Classify) - any other exception here is a genuine defect (e.g. a null-ref in the
            // resolver on some real pinned script shape), not a classification outcome.
            var classified = Classify(script);
            Assert.True(classified.Outcome is Outcome.Resolved or Outcome.UnsupportedConstruct or Outcome.Deferred,
                $"Item {itemId} ({aegisName}) produced an unclassifiable outcome.");
        }
        Assert.True(total > 15_000, $"Expected the real pinned item_db to contain over 15,000 eligible Weapon/Armor/Card rows; got {total}. " +
            "A much smaller count likely means the real generated pack failed to load or ItemCatalog.Current is stale.");
    }

    // Locked-in snapshot of the CURRENT resolved count. This number MUST only increase (a future increment widening
    // EquipmentBonusResolver's vocabulary) or stay the same (unrelated work) - a decrease means a prior commit's
    // bonus support regressed, which this test exists to catch. Do not "fix" a failure here by lowering the bound;
    // investigate what newly-unsupported construct appeared first.
    [Fact]
    public void Coverage_EligibleItemScripts_ResolvedCountNeverRegressesBelowTheLastVerifiedFloor()
    {
        var catalog = ItemCatalog.Current;
        var resolved = EligibleItemScripts(catalog).Count(entry => Classify(entry.Script).Outcome == Outcome.Resolved);
        // Verified floor as of PR #48 commit 4e7841a (2026-10-23, after autobonus/2/3 registration support):
        // 6183 of 17353 eligible item scripts resolve. The remaining ~11170 are real, common equip bonuses
        // using SP_* params beyond the current flat-bonus/bonus2/bonus3 vocabulary (bFlee, bCritical, bAspdRate,
        // bMatk, bAllStats, ... - 506 distinct pc_bonus* SP_* cases exist in pinned pc.cpp, this resolver
        // recognizes a small fraction), or an autobonus whose OUTER statement shape (if/variable-guarded) is
        // still unsupported - a real, substantial, separately-scoped gap, not a defect in this increment. See
        // feature16-s7-cards-combos-randomopts-scope.md memory for the full finding.
        Assert.True(resolved >= 6183, $"Eligible item script resolution regressed: expected at least 6183 resolved, got {resolved}.");
    }

    [Fact]
    public void Coverage_EveryRandomOption_IsClassified()
    {
        var catalog = ItemCatalog.Current;
        Assert.True(catalog.RandomOptions.Count > 0, "The real pinned item_randomopt_db.yml should have loaded at least one entry.");
        foreach (var (id, script) in catalog.RandomOptions)
        {
            var classified = Classify(script, new InventoryOption((short)id, 10, 0));
            Assert.True(classified.Outcome is Outcome.Resolved or Outcome.UnsupportedConstruct or Outcome.Deferred,
                $"Random option {id} produced an unclassifiable outcome.");
        }
    }

    // Locked-in floor: 189 of 249 random options resolve when probed with a representative nonzero option Value
    // (as of e593fb6). The remainder use the same unmodeled SP_* params as the wider item-script gap above (a few,
    // like bAtkEle, use a different non-delta single-constant argument shape entirely - see EquipmentBonusResolver's
    // Value() doc comment).
    [Fact]
    public void Coverage_RandomOptions_ResolvedCountNeverRegressesBelowTheLastVerifiedFloor()
    {
        var catalog = ItemCatalog.Current;
        var resolved = catalog.RandomOptions.Count(entry => Classify(entry.Value, new InventoryOption((short)entry.Key, 10, 0)).Outcome == Outcome.Resolved);
        Assert.True(resolved >= 189, $"Random-option resolution regressed: expected at least 189 resolved, got {resolved}.");
    }

    [Fact]
    public void Coverage_EveryCombo_IsClassified()
    {
        var catalog = ItemCatalog.Current;
        Assert.True(catalog.Combos.Count > 5_000, $"Expected the real pinned item_combos.yml to contain over 5,000 entries; got {catalog.Combos.Count}.");
        foreach (var combo in catalog.Combos)
        {
            var classified = Classify(combo.Script);
            Assert.True(classified.Outcome is Outcome.Resolved or Outcome.UnsupportedConstruct or Outcome.Deferred,
                "A combo script produced an unclassifiable outcome.");
        }
    }

    // Locked-in floor: 515 of 5114 combos resolve (as of e593fb6) - the much larger remaining gap (vs. the ~49% of
    // combos that are PURELY bonus/bonus2/bonus3 found during initial scoping) is explained by this count covering
    // EVERY combo regardless of its own member items' scripts: ComboResolver only determines ACTIVATION, but a
    // combo's bonus still only resolves if ITS OWN shared script uses supported SP_* params - the same wider
    // flat-bonus-vocabulary gap as the item-script floor above, not a combo-activation defect.
    [Fact]
    public void Coverage_Combos_ResolvedCountNeverRegressesBelowTheLastVerifiedFloor()
    {
        var catalog = ItemCatalog.Current;
        var resolved = catalog.Combos.Count(combo => Classify(combo.Script).Outcome == Outcome.Resolved);
        Assert.True(resolved >= 515, $"Combo script resolution regressed: expected at least 515 resolved, got {resolved}.");
    }

    // Every random option referenced by catalog.RandomOptions must have a real PackScript behind it (never a stale/
    // missing index) - a missing catalog entry is exactly the kind of silent gap the data-driven acceptance
    // requirement forbids.
    [Fact]
    public void Coverage_NoRandomOptionReferencesAMissingScript()
    {
        foreach (var (id, script) in ItemCatalog.Current.RandomOptions)
            Assert.NotNull(script); // FrozenDictionary<uint, PackScript> is non-nullable by construction, but this
                                     // locks in the invariant explicitly so a future refactor can't silently regress it.
    }
}
