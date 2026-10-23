using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using Xunit.Abstractions;

namespace Athena.Net.MapServer.Tests.World.Items;

// Coverage audit and gap inventory (data-driven acceptance requirement, docs/gameplay/feature-16-execution-plan.md) over
// the REAL committed ItemCatalog.Current: every script TryResolveEquipment can reach - every Weapon/Armor/Card item's own
// Script, every item_randomopt_db.yml entry, every item_combos.yml entry's shared Script - lands in exactly one category:
//
//   NoScript         - nothing to execute (not counted as coverage)
//   Live             - resolves, and at least one effect is applied by today's status calculation
//   AwaitingConsumer - resolves into typed, accumulated data only a Feature 17/18 (or not-yet-built) consumer reads
//   NoEffect         - resolves, but contributes nothing in the probe context (refine 0, no grade, nothing else worn,
//                      base-1 Novice with no skills):
//                      conditional scripts whose every effect needs a higher refine or another slot land here
//   Deferred         - the importer could not lower it (named construct + owner)
//   Unsupported      - lowered, but the equipment-bonus evaluator rejects it; the equipment cannot be worn at all
//                      (CharacterEquipmentMutationService.CanResolveAfter), so this is never silent effect loss
//
// Scope note: Usable/Healing/consumable on-use scripts are not equipment-bonus scripts and are excluded.
public sealed class EquipmentBonusCoverageTests(ITestOutputHelper output)
{
    private enum Category { NoScript, Live, AwaitingConsumer, NoEffect, Deferred, Unsupported }

    private readonly record struct Classified(Category Category, string? Blocker);

    private static readonly HashSet<string> LiveFlat = [.. EquipmentBonusParameters.ByName.Where(p => p.Value.Consumer == EquipmentBonusConsumer.LiveDerivedStats).Select(p => p.Key)];

    private static readonly EquipmentCharacter ProbeWearer = new(new CharacterGameplayState(CharacterId: 0, Version: 0, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 1, CurrentSp: 1, MaxHp: 1, MaxSp: 1, StatPoints: 0, SkillPoints: 0,
        Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1), CharacterSkillSnapshot.Empty, 1);

    private static Classified Classify(PackScript? script, InventoryOption option = default)
    {
        if (script is null) return new(Category.NoScript, null);
        if (script.Support == PackScriptSupport.Deferred) return new(Category.Deferred, $"deferred:{script.DeferredOwner}:{script.DeferredConstruct}");
        var bonuses = new EquipmentBonusAccumulator();
        // Nothing else worn (getequip* see empty slots), a base-1 Novice wearer with no skills; every branch is still validated.
        try { EquipmentBonusResolver.Accumulate(script, new EquipmentScriptContext(Option: option, EquippedAt: _ => null, Items: ItemCatalog.Current.ById, Character: ProbeWearer), bonuses); }
        catch (EquipmentBonusResolver.UnsupportedEquipScriptException ex) { return new(Category.Unsupported, ex.Message); }

        var p = bonuses.Param;
        var livePrimary = p.Strength != 0 || p.Agility != 0 || p.Vitality != 0 || p.Intelligence != 0 || p.Dexterity != 0 || p.Luck != 0;
        if (livePrimary || bonuses.Flat.Values.Any(v => LiveFlat.Contains(v.Key.Name) && v.Key.Hand != EquipmentBonusHand.Arrow)) return new(Category.Live, null);
        var anything = p != EquipmentParamBonus.None || bonuses.Flat.Values.Count > 0 || bonuses.Extended.Count > 0 || bonuses.Multi.Count > 0 || bonuses.Triggered.Count > 0
            || bonuses.ClientEffects.Count > 0;
        return new(anything ? Category.AwaitingConsumer : Category.NoEffect, null);
    }

    // Which feature owns the first blocking construct (ownership table of the execution plan).
    private static string OwnerOf(string blocker) => blocker switch
    {
        _ when blocker.StartsWith("command:skill") || blocker.StartsWith("command:itemskill") => "F18 skill grant",
        _ when blocker.StartsWith("command:sc_start") || blocker.StartsWith("command:sc_end") => "F17/F18 status effect",
        _ when blocker.StartsWith("command:hateffect") || blocker.StartsWith("command:specialeffect") => "client effect (no server state)",
        _ when blocker.StartsWith("deferred:GeneralScript") => "F20 general script",
        _ when blocker.StartsWith("deferred:") => "deferred (importer)",
        _ when blocker.StartsWith("bonus-param:") => "F16 vocabulary (not a pc_bonus case)",
        _ when blocker.Contains("-unknown-skill:") => "invalid source data (pinned skips it with an error)",
        _ => "F16 evaluator (control flow / locals / context)",
    };

    private static IEnumerable<(string Population, PackScript? Script, InventoryOption Option)> Populations(ItemCatalog catalog)
    {
        foreach (var def in catalog.ById.Values)
            if (def is WeaponItemDefinition or ArmorItemDefinition or CardItemDefinition)
                yield return (def switch { WeaponItemDefinition => "Weapon", ArmorItemDefinition => "Armor", _ => "Card" }, def.Meta?.Script, default);
        foreach (var (id, script) in catalog.RandomOptions) yield return ("RandomOption", script, new InventoryOption((short)id, 10, 0));
        foreach (var combo in catalog.Combos) yield return ("Combo", combo.Script, default);
    }

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<Classified>>> Measured = new(() =>
        Populations(ItemCatalog.Current).GroupBy(p => p.Population)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Classified>)[.. g.Select(p => Classify(p.Script, p.Option))]));

    private static int Count(string population, params Category[] categories) => Measured.Value[population].Count(c => categories.Contains(c.Category));

    [Fact]
    public void Inventory_EveryReachableScriptIsClassified_AndTheBreakdownIsReported()
    {
        Assert.True(Measured.Value["Weapon"].Count + Measured.Value["Armor"].Count + Measured.Value["Card"].Count > 15_000,
            "The real pinned item_db should contain over 15,000 Weapon/Armor/Card rows; a smaller count means the pack failed to load.");
        Assert.True(Measured.Value["Combo"].Count > 5_000);
        Assert.True(Measured.Value["RandomOption"].Count > 0);

        foreach (var (population, classified) in Measured.Value.OrderBy(p => p.Key))
        {
            var scripted = classified.Count(c => c.Category != Category.NoScript);
            output.WriteLine($"{population}: total={classified.Count} noScript={Count(population, Category.NoScript)} scripted={scripted} " +
                $"live={Count(population, Category.Live)} awaitingConsumer={Count(population, Category.AwaitingConsumer)} noEffect={Count(population, Category.NoEffect)} " +
                $"deferred={Count(population, Category.Deferred)} unsupported={Count(population, Category.Unsupported)}");
            foreach (var blocker in classified.Where(c => c.Blocker is not null).GroupBy(c => c.Blocker!).OrderByDescending(g => g.Count()).Take(15))
                output.WriteLine($"   {blocker.Count(),6}  {blocker.Key}  [{OwnerOf(blocker.Key)}]");
        }
    }

    // Floors over SCRIPTED entries only (Live + AwaitingConsumer + NoEffect): a script-less item never counts as coverage.
    // Measured after the S7 evaluator increments (locals, if/else, equipment and wearer context). These must only rise; a drop means a
    // construct regressed - investigate it, never lower the bound.
    [Theory]
    [InlineData("Weapon", 2241)]
    [InlineData("Armor", 4884)]
    [InlineData("Card", 4790)]
    [InlineData("RandomOption", 248)]
    [InlineData("Combo", 4609)]
    public void Coverage_ResolvedScriptedEntries_NeverRegressBelowTheVerifiedFloor(string population, int floor)
    {
        var resolved = Count(population, Category.Live, Category.AwaitingConsumer, Category.NoEffect);
        Assert.True(resolved >= floor, $"{population}: expected at least {floor} resolved scripted entries, got {resolved}.");
    }

    // The original S7 floors (counting script-less items as resolved), kept so the earlier contract still holds.
    [Fact]
    public void Coverage_OriginalS7Floors_StillHold()
    {
        Assert.True(Count("Weapon", Category.NoScript, Category.Live, Category.AwaitingConsumer, Category.NoEffect)
            + Count("Armor", Category.NoScript, Category.Live, Category.AwaitingConsumer, Category.NoEffect)
            + Count("Card", Category.NoScript, Category.Live, Category.AwaitingConsumer, Category.NoEffect) >= 6183);
        Assert.True(Count("RandomOption", Category.Live, Category.AwaitingConsumer, Category.NoEffect) >= 189);
        Assert.True(Count("Combo", Category.Live, Category.AwaitingConsumer, Category.NoEffect) >= 515);
    }

    // Every random option referenced by catalog.RandomOptions must have a real PackScript behind it.
    [Fact]
    public void Coverage_NoRandomOptionReferencesAMissingScript()
    {
        foreach (var (_, script) in ItemCatalog.Current.RandomOptions) Assert.NotNull(script);
    }
}
