using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Iro;
using Athena.Net.MapServer.Gameplay.Rules.Kro;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

// Test composition helpers. Fixtures that do not care about the region use the default (iRO, Renewal) rules, composed through
// the production factory; region-specific tests name the region explicitly. Roll seams make damage rolls deterministic
// without changing the algorithm.
internal static class TestGameplayRules
{
    public static GameplayRuleServices Services(GameplayRegion region = GameplayRegion.Iro) =>
        GameplayRulesFactory.Create(new GameplayOptions { Region = region });

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
