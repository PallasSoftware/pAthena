using Athena.Net.MapServer.Generated.Jobs;

namespace Athena.Net.MapServer.Gameplay.Rules.Renewal;

// Which job classes the Renewal status calculation is VERIFIED for. A job is listed only when fixture VF-16 (real
// status_calc_pc runs for 44 classes at two level pairs: six stats, derived stats, max HP/SP) is reproduced exactly by
// StatusCalculator / DerivedStatCalculator. Everything else is Unverified, never silently "supported":
//   - ParameterRules.UpperMultiplier: transcendent/fourth classes multiply max HP/SP by 1.25 (status_calc_maxhp_pc) - not modeled.
//   - ParameterRules.FourthTraits: fourth-job POW/STA/WIS/SPL/CON/CRT traits feed BATK/HIT/FLEE/MATK - not modeled (Epic 15 deferral).
//   - ParameterRules.NoDeathBonus: Super Novice-line +10 all stats needs the character's die_counter (not tracked). Super
//     Novice itself is verified only below job level 70 (VF-16 uses job level 1 and 10); at 70+ it carries this rule too.
//   - ParameterRules.SpTableMismatch: generated SP table differs from the reference at level 1 (Kagerou) - to be investigated.
public static class JobSupport
{
    [Flags]
    public enum ParameterRules { None = 0, UpperMultiplier = 1, FourthTraits = 2, NoDeathBonus = 4, SpTableMismatch = 8 }

    // Known divergences observed in VF-16. Jobs absent from both tables have no fixture yet and are Unverified too.
    private static readonly Dictionary<JobClass, ParameterRules> KnownDivergent = new()
    {
        [JobClass.NoviceHigh] = ParameterRules.UpperMultiplier,
        [JobClass.SwordmanHigh] = ParameterRules.UpperMultiplier,
        [JobClass.MageHigh] = ParameterRules.UpperMultiplier,
        [JobClass.LordKnight] = ParameterRules.UpperMultiplier,
        [JobClass.HighWizard] = ParameterRules.UpperMultiplier,
        [JobClass.DragonKnight] = ParameterRules.UpperMultiplier | ParameterRules.FourthTraits,
        [JobClass.ArchMage] = ParameterRules.UpperMultiplier | ParameterRules.FourthTraits,
        [JobClass.Kagerou] = ParameterRules.SpTableMismatch,
        [JobClass.HyperNovice] = ParameterRules.NoDeathBonus,
    };

    // Jobs reproduced exactly by VF-16 (see JobSupportTests, which re-derives this set from the fixture).
    private static readonly HashSet<JobClass> Verified =
    [
        JobClass.Novice, JobClass.Swordman, JobClass.Mage, JobClass.Archer, JobClass.Acolyte, JobClass.Merchant, JobClass.Thief,
        JobClass.Knight, JobClass.Priest, JobClass.Wizard, JobClass.Blacksmith, JobClass.Hunter, JobClass.Assassin, JobClass.Crusader,
        JobClass.Monk, JobClass.Sage, JobClass.Rogue, JobClass.Alchemist, JobClass.Bard, JobClass.Dancer, JobClass.SuperNovice,
        JobClass.Gunslinger, JobClass.Ninja, JobClass.Taekwon, JobClass.StarGladiator, JobClass.SoulLinker, JobClass.Baby,
        JobClass.BabySwordman, JobClass.BabyKnight, JobClass.RuneKnight, JobClass.Warlock, JobClass.ArchBishop, JobClass.Summoner,
        JobClass.SuperBaby, JobClass.Rebellion,
    ];

    public static bool IsReferenceVerified(ushort jobClass) => Verified.Contains((JobClass)jobClass);

    public static ParameterRules KnownDivergence(ushort jobClass) => KnownDivergent.GetValueOrDefault((JobClass)jobClass);
}
