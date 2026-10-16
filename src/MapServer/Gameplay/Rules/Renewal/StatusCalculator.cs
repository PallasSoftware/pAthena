using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Renewal;

// Base-status stage of the pinned status_calc_pc_sub six-stat assembly (status.cpp:4249-4301): the job-level bonus
// from job_stats.yml BonusStats (generated as cumulative one-based-by-job-level arrays, CharacterProgressionDefinition)
// is added to base_status, and the character's allocated stat is added on top. Statuses are a later overlay
// (CharacterStatusEffectState). Fixture VF-02 pins the reference output for Novice job levels 1..10.
internal static class StatusCalculator
{
    public readonly record struct PrimaryStats(ushort Strength, ushort Agility, ushort Vitality, ushort Intelligence, ushort Dexterity, ushort Luck);

    public static PrimaryStats BasePrimaryStats(CharacterGameplayState state)
    {
        var job = GeneratedProgressionRegistry.Get(state.JobClass);
        if (state.JobLevel is 0 || state.JobLevel > job.MaxJobLevel)
            throw new InvalidOperationException("Character job level is outside the generated progression range.");
        var level = state.JobLevel;
        return new PrimaryStats(
            Add(state.Strength, job.JobStrengthBonus[level]), Add(state.Agility, job.JobAgilityBonus[level]),
            Add(state.Vitality, job.JobVitalityBonus[level]), Add(state.Intelligence, job.JobIntelligenceBonus[level]),
            Add(state.Dexterity, job.JobDexterityBonus[level]), Add(state.Luck, job.JobLuckBonus[level]));
    }

    // status.cpp:4291-4301 clamps the sum to 0..USHRT_MAX; allocated stats are already capped at pc_maxparameter.
    private static ushort Add(ushort allocated, uint jobBonus) => (ushort)Math.Min((uint)allocated + jobBonus, ushort.MaxValue);
}
