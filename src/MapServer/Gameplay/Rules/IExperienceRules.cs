using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Regional experience/level-up rules: threshold crossing, overcarry cap, stat/skill points and the resource refill on
// level-up. Experience RATES (server configuration policy) stay in Gameplay.Rates and are not regional rules.
public interface IExperienceRules
{
    // `resources` is the wearer whose new maxima a level-up computes (and fully heals to on a base level).
    CharacterProgressionResult CalculateProgression(CharacterGameplayState state, ulong baseAward, ulong jobAward, MaxResourceContext resources = default);
}
