using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Iro;
using Athena.Net.MapServer.Gameplay.Rules.Kro;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

// Test composition helpers. Fixtures that do not care about the region use the default (iRO, Renewal) rules, composed through
// the production factory; region-specific tests name the region explicitly. Roll seams make damage rolls deterministic
// without changing the algorithm.
internal static class TestGameplayRules
{
    public static GameplayRuleServices Services(GameplayRegion region = GameplayRegion.Iro) =>
        GameplayRulesFactory.Create(new GameplayOptions { Region = region });

    // A MonsterCombatCoordinator that does not care about mob drops uses this - an AegisName lookup
    // with nothing in it resolves every MobDropEntry to "unknown item", so MobDropResolver.ResolveDrops
    // always yields an empty list, exactly like `new QuestDropResolver([])` already means "no quest
    // drops configured" for the same fixtures.
    public static readonly MobDropResolver NoMobDrops = new(new Dictionary<string, ItemDefinition>());

    public static ICombatRules Combat(Func<int, int, int>? rollWeaponAtk = null, Func<int, int, int>? rollMonsterAtk = null, GameplayRegion region = GameplayRegion.Iro) =>
        region == GameplayRegion.Iro ? new IroCombatRules(rollWeaponAtk, rollMonsterAtk) : new KroCombatRules(rollWeaponAtk, rollMonsterAtk);

    // The default bundle with the combat rules replaced by one with deterministic rolls.
    public static GameplayRuleServices Services(Func<int, int, int>? rollWeaponAtk, Func<int, int, int>? rollMonsterAtk = null, GameplayRegion region = GameplayRegion.Iro) =>
        Services(region) with { Combat = Combat(rollWeaponAtk, rollMonsterAtk, region) };

    public static CharacterStatusEffectState StatusEffects(TimeProvider timeProvider, GameplayRegion region = GameplayRegion.Iro)
    {
        var rules = Services(region);
        return new CharacterStatusEffectState(timeProvider, rules.StatusEffects, rules.Stats);
    }
}
