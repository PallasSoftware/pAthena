using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

public readonly record struct CharacterProgressionResult(
    CharacterGameplayState Before,
    CharacterGameplayState After,
    ushort BaseLevelsGained,
    ushort JobLevelsGained,
    ulong NextBaseExperience,
    ulong NextJobExperience,
    ulong BaseExperienceAwarded = 0,
    ulong JobExperienceAwarded = 0);

// Owns ONLY Base/Job EXP state, thresholds, Base/Job Level, stat/skill points,
// HP/SP recalculation, max levels, and the EXP remainder/overcarry policy, plus
// the one atomic persisted state transition. It receives already-rated final
// Base/Job EXP values and does not know or care whether the reward came from a
// monster, a script, an MVP, or an event - rate selection happens strictly
// upstream, in Athena.Net.MapServer.Gameplay.Rates (GameplayRateResolver /
// ExperienceRewardService), before this service is ever called.
public sealed class CharacterProgressionService(CharacterGameplayStateSession stateSession, IExperienceRules experienceRules)
{

    public async Task<CharacterProgressionResult?> AddExperienceAsync(
        ulong baseExperience,
        ulong jobExperience,
        CancellationToken cancellationToken)
    {
        var before = stateSession.State;
        var calculation = experienceRules.CalculateProgression(before, baseExperience, jobExperience);
        if (calculation.After == before) return calculation;
        var persisted = await stateSession.MutateAsync(_ => calculation.After, cancellationToken, reason: "experience");
        return persisted is null ? null : calculation with { After = persisted };
    }
}
