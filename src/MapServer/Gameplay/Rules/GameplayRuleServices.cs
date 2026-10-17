namespace Athena.Net.MapServer.Gameplay.Rules;

// The gameplay rule implementations selected once at the composition root for this MapServer deployment (one region, one
// ruleset). Consumers receive the abstractions - through this bundle or individual constructor parameters - and never
// reference a concrete regional class or ask which region is active.
public sealed record GameplayRuleServices(
    ICombatRules Combat,
    IStatRules Stats,
    ITimingRules Timing,
    IStatusEffectRules StatusEffects,
    IExperienceRules Experience);
