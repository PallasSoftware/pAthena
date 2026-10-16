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

    // status_calc_maxhp_pc / status_calc_maxsp_pc (status.cpp:3511-3600) for the no-equipment/no-skill scope:
    // double table * (1.0 + stat * 0.01), truncated to uint32 and floored at 1. The table is the generated
    // job_basepoints/HP table (one-based by base level). The x1.25 upper/fourth-class and Taekwon-ranker multipliers
    // are not modeled (Novice-line fixtures only); status_get_hpbonus equipment/skill terms are zero here.
    public static uint MaxHp(CharacterGameplayState state, int effectiveVitality) => ScaleResource(GeneratedProgressionRegistry.Get(state.JobClass).BaseHp[state.BaseLevel], effectiveVitality);

    public static uint MaxSp(CharacterGameplayState state, int effectiveIntelligence) => ScaleResource(GeneratedProgressionRegistry.Get(state.JobClass).BaseSp[state.BaseLevel], effectiveIntelligence);

    // pc_statusup -> status_calc_pc (SCO_NONE): maxima are rebuilt from the new VIT/INT and the CURRENT values are only
    // clamped down to the new maxima - raising a maximum never heals, lowering one never leaves current above it
    // (battle_status.hp/sp in fixture VF-11). Returns the state unchanged when nothing moves.
    public static CharacterGameplayState WithRecalculatedResources(CharacterGameplayState state)
    {
        var stats = BasePrimaryStats(state);
        var maxHp = MaxHp(state, stats.Vitality);
        var maxSp = MaxSp(state, stats.Intelligence);
        return state with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = Math.Min(state.CurrentHp, maxHp), CurrentSp = Math.Min(state.CurrentSp, maxSp) };
    }

    private static uint ScaleResource(uint table, int stat)
    {
        double max = table;
        if (stat > 0) max *= 1.0 + stat * 0.01;
        return max < 1.0 ? 1u : (uint)max;
    }
}
