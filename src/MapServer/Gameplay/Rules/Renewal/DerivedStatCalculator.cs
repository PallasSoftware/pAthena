using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Renewal;

public readonly record struct DerivedStats(int BaseAttack, int Hit, int Flee, int PerfectFlee, int Critical, int SoftDefense, int SoftMagicDefense, int HardDefense = 0);

// Renewal PC derived stats for the supported scope: armor hard DEF only from equipment (no refine/cards/scripts), no passives or map penalties, and only the
// Blessing / Increase AGI overlays. Mirrors status_calc_misc (base_status, status.cpp:2600-2746) and the delta
// path of status_calc_bl_main (status.cpp:5964-6049). Integer divisions, float promotions and clamps are kept
// exactly as in the source; fixtures VF-01/02/07/08 are the independent check. MATK, HP/SP and ASPD are separate.
internal static class DerivedStatCalculator
{
    private const int ShortMax = short.MaxValue;

    public static DerivedStats Calculate(CharacterStatusSnapshot snapshot, EquipmentStats equipment = default)
    {
        var level = snapshot.State.BaseLevel;
        var b = StatusCalculator.BasePrimaryStats(snapshot.State);
        var e = snapshot.Effective;
        var baseDerived = BaseDerived(level, b);

        // status_calc_bl_main only rebuilds BATK when the base value is non-zero (status.cpp:5964).
        var batk = baseDerived.BaseAttack;
        if (baseDerived.BaseAttack != 0)
            batk = Math.Clamp(BaseAtk(level, e.Strength, e.Dexterity, e.Luck) + (baseDerived.BaseAttack - BaseAtk(level, b.Strength, b.Dexterity, b.Luck)), 0, int.MaxValue);

        var hit = baseDerived.Hit;
        if (e.Dexterity != b.Dexterity || e.Luck != b.Luck) hit += (e.Dexterity - b.Dexterity) + (e.Luck / 3 - b.Luck / 3);
        hit = Math.Clamp(hit + e.HitBonus, 1, ShortMax);

        var flee = baseDerived.Flee;
        if (e.Agility != b.Agility || e.Luck != b.Luck) flee += (e.Agility - b.Agility) + (e.Luck / 5 - b.Luck / 5);
        flee = Math.Clamp(flee, 1, ShortMax);

        var def2 = baseDerived.SoftDefense;
        if (e.Vitality != b.Vitality || e.Agility != b.Agility)
            def2 += (int)(((float)e.Vitality / 2 - (float)b.Vitality / 2) + ((float)e.Agility / 5 - (float)b.Agility / 5));
        def2 = Math.Clamp(def2, short.MinValue, ShortMax);

        var mdef2 = baseDerived.SoftMagicDefense;
        if (e.Intelligence != b.Intelligence || e.Vitality != b.Vitality || e.Dexterity != b.Dexterity)
            mdef2 += (e.Intelligence - b.Intelligence) + (int)(((float)e.Dexterity / 5 - (float)b.Dexterity / 5) + ((float)e.Vitality / 5 - (float)b.Vitality / 5));
        mdef2 = Math.Clamp(mdef2, short.MinValue, ShortMax);

        // CRI and perfect FLEE have no overlay from the supported statuses (LUK is unchanged by them).
        // Hard DEF = summed equipped-armor DEF; status_calc_def with no active DEF status is cap_value(def, SHRT_MIN, SHRT_MAX).
        var hardDef = Math.Clamp(equipment.HardDefense, short.MinValue, ShortMax);
        return baseDerived with { BaseAttack = batk, Hit = hit, Flee = flee, SoftDefense = def2, SoftMagicDefense = mdef2, HardDefense = hardDef };
    }

    // status_calc_misc, RENEWAL, BL_PC (status.cpp:2638-2745).
    private static DerivedStats BaseDerived(int level, StatusCalculator.PrimaryStats s)
    {
        var hit = Math.Clamp(level + s.Dexterity + s.Luck / 3 + 175, 1, ShortMax);
        var flee = Math.Clamp(level + s.Agility + s.Luck / 5 + 100, 1, ShortMax);
        var def2 = Math.Clamp((int)(((float)level + s.Vitality) / 2 + (float)s.Agility / 5), 0, ShortMax);
        var mdef2 = Math.Clamp((int)(s.Intelligence + (float)level / 4 + (float)(s.Dexterity + s.Vitality) / 5), 0, ShortMax);
        var cri = Math.Clamp(level / 10 + 10 + s.Luck * 3, 1, ShortMax);
        var flee2 = Math.Clamp(s.Luck + 10, 0, ShortMax);
        return new DerivedStats(BaseAtk(level, s.Strength, s.Dexterity, s.Luck), hit, flee, flee2, cri, def2, mdef2);
    }

    // status_base_atk, RENEWAL BL_PC non-ranged weapon, POW = 0 (status.cpp:2475-2477); clamp 0..USHRT_MAX.
    private static int BaseAtk(int level, int str, int dex, int luk) =>
        Math.Clamp((str * 10 + dex * 10 / 5 + luk * 10 / 3 + level * 10 / 4) / 10, 0, ushort.MaxValue);
}
