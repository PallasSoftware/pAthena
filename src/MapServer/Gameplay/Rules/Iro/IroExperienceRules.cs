using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Iro;

// iRO gameplay rules. Independently maintained: this class must not inherit from, delegate to or share a
// concrete algorithm with its counterpart in the other region. Behavior currently reproduces the pinned rAthena
// (e985006171d2eb320ee512a653f4c83aea3d81b6) Renewal source exactly; official iRO behavior is NOT verified.
//
// pc_checkbaselevelup/pc_checkjoblevelup with exp.conf multi_level_up = no: one threshold per grant, overcarry capped to
// threshold - 1, cumulative stat-point table, +1 skill point per job level, and the resource refill on level-up using the
// injected region's stat rules (maxima from status_calc_maxhp_pc/maxsp_pc). Experience RATES are configuration, not here.
public sealed class IroExperienceRules(IStatRules statRules) : IExperienceRules
{
    private const ulong MaximumExperience = long.MaxValue;
    private const ulong MaximumBaseLevelExperience = 99_999_999;
    private const ulong MaximumJobLevelExperience = 999_999_999;

    public CharacterProgressionResult CalculateProgression(CharacterGameplayState state, ulong baseAward, ulong jobAward, MaxResourceContext resources = default)
    {
        var definition = GeneratedProgressionRegistry.Get(state.JobClass);
        if (state.BaseLevel is 0 || state.BaseLevel > definition.MaxBaseLevel ||
            state.JobLevel is 0 || state.JobLevel > definition.MaxJobLevel)
            throw new InvalidOperationException("Character levels are outside the generated progression range.");

        var baseLevel = state.BaseLevel;
        var jobLevel = state.JobLevel;
        var effectiveBaseAward = baseLevel == definition.MaxBaseLevel
            ? Math.Min(baseAward, MaximumBaseLevelExperience - Math.Min(state.BaseExperience, MaximumBaseLevelExperience))
            : baseAward;
        var effectiveJobAward = jobLevel == definition.MaxJobLevel
            ? Math.Min(jobAward, MaximumJobLevelExperience - Math.Min(state.JobExperience, MaximumJobLevelExperience))
            : jobAward;
        var baseExperience = SaturatingAdd(state.BaseExperience, effectiveBaseAward);
        var jobExperience = SaturatingAdd(state.JobExperience, effectiveJobAward);
        var statPoints = state.StatPoints;
        var skillPoints = state.SkillPoints;
        ushort baseLevelsGained = 0;
        ushort jobLevelsGained = 0;

        // Pinned exp.conf defaults multi_level_up to no. pc_checkbaselevelup crosses one
        // threshold and caps overcarry to that crossed threshold minus one.
        if (baseLevel < definition.MaxBaseLevel)
        {
            var required = definition.BaseExperienceToNext[baseLevel];
            if (baseExperience >= required)
            {
                baseExperience -= required;
                if (baseExperience > required - 1) baseExperience = required - 1;
                statPoints = checked(statPoints + definition.CumulativeStatPoints[baseLevel + 1] - definition.CumulativeStatPoints[baseLevel]);
                baseLevel++;
                baseLevelsGained = 1;
            }
        }
        if (baseLevel == definition.MaxBaseLevel) baseExperience = Math.Min(baseExperience, MaximumBaseLevelExperience);

        if (jobLevel < definition.MaxJobLevel)
        {
            var required = definition.JobExperienceToNext[jobLevel];
            if (jobExperience >= required)
            {
                jobExperience -= required;
                if (jobExperience > required - 1) jobExperience = required - 1;
                jobLevel++;
                jobLevelsGained = 1;
                skillPoints = checked(skillPoints + 1);
            }
        }
        if (jobLevel == definition.MaxJobLevel) jobExperience = Math.Min(jobExperience, MaximumJobLevelExperience);

        var after = state with
        {
            BaseLevel = baseLevel,
            JobLevel = jobLevel,
            BaseExperience = baseExperience,
            JobExperience = jobExperience,
            StatPoints = statPoints,
            SkillPoints = skillPoints,
        };

        if (baseLevelsGained > 0 || jobLevelsGained > 0)
        {
            // The post-level state carries the new BaseLevel; the maxima include the wearer's equipment and skills (pinned
            // pc_checkbaselevelup runs status_calc_pc, then status_percent_heal to the new maximum).
            var (maxHp, maxSp) = statRules.MaxResources(after, resources);
            after = baseLevelsGained > 0
                ? after with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = maxHp, CurrentSp = maxSp }
                : after with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = Math.Min(after.CurrentHp, maxHp), CurrentSp = Math.Min(after.CurrentSp, maxSp) };
        }

        return new(state, after, baseLevelsGained, jobLevelsGained,
            baseLevel < definition.MaxBaseLevel ? definition.BaseExperienceToNext[baseLevel] : MaximumBaseLevelExperience,
            jobLevel < definition.MaxJobLevel ? definition.JobExperienceToNext[jobLevel] : MaximumJobLevelExperience,
            effectiveBaseAward,
            effectiveJobAward);
    }

    private static ulong SaturatingAdd(ulong current, ulong award) =>
        MaximumExperience - Math.Min(current, MaximumExperience) < award ? MaximumExperience : current + award;
}
