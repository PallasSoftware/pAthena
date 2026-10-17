using Athena.Net.MapServer.Gameplay.Rules.Iro;
using Athena.Net.MapServer.Gameplay.Rules.Kro;

namespace Athena.Net.MapServer.Gameplay.Rules;

// The one place MapServer decides which regional/ruleset gameplay implementations to register, called once from the
// composition root (MapServerApp registers the result with the host's service collection via AddGameplayRules).
// Every consumer downstream depends only on the ICombatRules/IStatRules/ITimingRules/IStatusEffectRules/IExperienceRules
// abstractions. The two regions have completely separate concrete implementations (no sharing, inheritance or delegation
// between them), so each can evolve independently; they currently reproduce the same pinned rAthena Renewal behavior.
//
// PreRenewal is a real enum member so configuration/domain code can express the intent explicitly (RagnarokRuleSet's own
// doc comment), but it has NO implementation in this codebase yet (see Gameplay/Rules/PreRenewal/README.md) - selecting it
// must fail startup loudly, never silently fall back to Renewal and never return a fake/stub rules object. Likewise an
// unknown region fails; the three dimensions (ruleset, region, client protocol) are selected independently.
public static class GameplayRulesFactory
{
    public static GameplayRuleServices Create(GameplayOptions options)
    {
        if (options.RuleSet != RagnarokRuleSet.Renewal)
        {
            throw options.RuleSet == RagnarokRuleSet.PreRenewal
                ? new NotSupportedException("Pre-Renewal gameplay rules are not implemented.")
                : new NotSupportedException($"Unknown gameplay rule set '{options.RuleSet}'.");
        }

        return options.Region switch
        {
            GameplayRegion.Iro => CreateIro(),
            GameplayRegion.Kro => CreateKro(),
            _ => throw new NotSupportedException($"Unknown gameplay region '{options.Region}'."),
        };
    }

    private static GameplayRuleServices CreateIro()
    {
        var stats = new IroStatRules();
        return new GameplayRuleServices(new IroCombatRules(), stats, new IroTimingRules(), new IroStatusEffectRules(), new IroExperienceRules(stats));
    }

    private static GameplayRuleServices CreateKro()
    {
        var stats = new KroStatRules();
        return new GameplayRuleServices(new KroCombatRules(), stats, new KroTimingRules(), new KroStatusEffectRules(), new KroExperienceRules(stats));
    }
}
