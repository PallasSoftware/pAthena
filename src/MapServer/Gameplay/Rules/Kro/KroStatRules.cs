using Athena.Net.MapServer.Generated.GameData.Refine;
using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Gameplay.Rules.Kro;

// kRO gameplay rules. Independently maintained: this class must not inherit from, delegate to or share a
// concrete algorithm with its counterpart in the other region. Behavior currently reproduces the pinned rAthena
// (e985006171d2eb320ee512a653f4c83aea3d81b6) Renewal source exactly; official kRO behavior is NOT verified.
//
// Six stats: status_calc_pc_sub job bonus assembly (status.cpp:4249-4301). Derived stats: status_calc_misc
// (status.cpp:2600-2746) plus the status_calc_bl_main delta path (status.cpp:5964-6049) and RENEWAL MATK (6283-6361).
// Max HP/SP: status_calc_maxhp_pc/maxsp_pc (status.cpp:3511-3600), double table * (1 + stat*0.01) truncated.
// Equipment: each equipped armor row's DEF once, right-hand weapon. Allocation cost: PC_STATUS_POINT_COST.
// Fixtures VF-01/02/04/05/07/08/11/16 are the independent check. x1.25 upper/fourth multipliers, fourth-job traits and
// the Super Novice no-death bonus are NOT modeled (see JobSupport).
public sealed class KroStatRules : IStatRules
{
    private const int ShortMax = short.MaxValue;

    public PrimaryStats BasePrimaryStats(CharacterGameplayState state)
    {
        var job = GeneratedProgressionRegistry.Get(state.JobClass);
        if (state.JobLevel is 0 || state.JobLevel > job.MaxJobLevel)
            throw new InvalidOperationException("Character job level is outside the generated progression range.");
        var level = state.JobLevel;
        return new PrimaryStats(
            AddBonus(state.Strength, job.JobStrengthBonus[level]), AddBonus(state.Agility, job.JobAgilityBonus[level]),
            AddBonus(state.Vitality, job.JobVitalityBonus[level]), AddBonus(state.Intelligence, job.JobIntelligenceBonus[level]),
            AddBonus(state.Dexterity, job.JobDexterityBonus[level]), AddBonus(state.Luck, job.JobLuckBonus[level]));
    }

    // status.cpp:4291-4301 clamps the sum to 0..USHRT_MAX; allocated stats are already capped at pc_maxparameter.
    private static ushort AddBonus(ushort allocated, uint jobBonus) => (ushort)Math.Min((uint)allocated + jobBonus, ushort.MaxValue);

    // status.cpp:4291-4301: base + status + param_bonus (equip flat bonuses) + param_equip, each capped to 0..USHRT_MAX
    // independently before any derived stat reads it. param_equip (bonus2-5/card/combo territory) is not modeled yet.
    private static PrimaryStats ApplyParamBonus(PrimaryStats s, EquipmentParamBonus bonus) => new(
        Cap(s.Strength + bonus.Strength), Cap(s.Agility + bonus.Agility), Cap(s.Vitality + bonus.Vitality),
        Cap(s.Intelligence + bonus.Intelligence), Cap(s.Dexterity + bonus.Dexterity), Cap(s.Luck + bonus.Luck));

    private static ushort Cap(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    public DerivedStats CalculateDerivedStats(CharacterStatusSnapshot snapshot, EquipmentStats equipment = default)
    {
        var level = snapshot.State.BaseLevel;
        var b = ApplyParamBonus(BasePrimaryStats(snapshot.State), equipment.ParamBonus);
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

        // Hard DEF = summed equipped-armor DEF plus the refine bonus (status.cpp:4096's own
        // `(refinedef+50)/100` truncation, applied once to the SUM of every equipped armor row's
        // own refine bonus, not per-row); status_calc_def with no active DEF status is
        // cap_value(def, SHRT_MIN, SHRT_MAX).
        var hardDef = Math.Clamp(equipment.HardDefense + (equipment.ArmorRefineDefense + 50) / 100, short.MinValue, ShortMax);

        // RENEWAL MATK from the EFFECTIVE stats; weapon MATK, %, pseudo-buffs and equipment MATK are not modeled, so a
        // weapon with MagicAttack is rejected rather than ignored.
        if ((equipment.Weapon?.MagicAttack ?? 0) != 0) throw new NotSupportedException("Weapon MATK is not modeled.");
        var matk = Math.Clamp(e.Intelligence + e.Intelligence / 2 + e.Dexterity / 5 + e.Luck / 3 + level / 4 + equipment.WeaponRefineMatk, 0, ushort.MaxValue);

        // CRI and perfect FLEE have no overlay from the supported statuses (LUK is unchanged by them).
        return baseDerived with { BaseAttack = batk, Hit = hit, Flee = flee, SoftDefense = def2, SoftMagicDefense = mdef2, HardDefense = hardDef, MagicAttackMin = matk, MagicAttackMax = matk };
    }

    // status_calc_misc, RENEWAL, BL_PC (status.cpp:2638-2745).
    private static DerivedStats BaseDerived(int level, PrimaryStats s)
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

    public uint MaxHp(CharacterGameplayState state, int effectiveVitality) => ScaleResource(GeneratedProgressionRegistry.Get(state.JobClass).BaseHp[state.BaseLevel], effectiveVitality);

    public uint MaxSp(CharacterGameplayState state, int effectiveIntelligence) => ScaleResource(GeneratedProgressionRegistry.Get(state.JobClass).BaseSp[state.BaseLevel], effectiveIntelligence);

    private static uint ScaleResource(uint table, int stat)
    {
        double max = table;
        if (stat > 0) max *= 1.0 + stat * 0.01;
        return max < 1.0 ? 1u : (uint)max;
    }

    // pc_statusup -> status_calc_pc (SCO_NONE): maxima are rebuilt from the new VIT/INT and the CURRENT values are only
    // clamped down to the new maxima - raising a maximum never heals (fixture VF-11).
    public CharacterGameplayState WithRecalculatedResources(CharacterGameplayState state)
    {
        var stats = BasePrimaryStats(state);
        var maxHp = MaxHp(state, stats.Vitality);
        var maxSp = MaxSp(state, stats.Intelligence);
        return state with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = Math.Min(state.CurrentHp, maxHp), CurrentSp = Math.Min(state.CurrentSp, maxSp) };
    }

    // Pinned status_calc_pc's equipment loop (status.cpp:3927-4015,4096), scoped to the refine
    // formula's flat-Bonus half only: weapon `wa->atk2 += info->bonus/100` and (sd->weapontype1 !=
    // W_BOW) `wa->matk += info->bonus/100`, armor `refinedef += info->bonus` folded into
    // `base_status->def += (refinedef+50)/100` by the caller. Gated on ItemMetadata.Refineable
    // (pinned findLevelInfoSub's own `if (data.flag.no_refine) return nullptr;` early-out,
    // status.cpp:417-419) so a non-refineable row's Refine byte (never expected nonzero, but never
    // trusted either) can never contribute a bonus. The enchant-grade multiplier
    // (`(info->bonus/100) * enchantgrade_info->bonus / 100`) is explicitly NOT modeled yet -
    // enchantgrade_db is not generated (see docs/gameplay/feature-16-execution-plan.md's S6 entry) -
    // so a graded item (EnchantGrade > 0) is rejected rather than silently applying refine-only.
    // Shadow gear refine (a third GeneratedRefineBonus group this project never generates) and the
    // weapon-level-5 pAtk/sMatk bonus are likewise out of this slice's scope.
    public bool TryResolveEquipment(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items, out EquipmentStats equipment)
    {
        var hardDefense = 0;
        WeaponItemDefinition? weapon = null;
        var paramBonus = EquipmentParamBonus.None;
        var weaponRefineAtk = 0;
        var weaponRefineMatk = 0;
        var armorRefineDefense = 0;
        foreach (var row in inventory.Items)
        {
            if (row.Equip == 0) continue; // One inventory row counts once however many slot bits it carries.
            if (!items.TryGetValue(row.ItemId, out var definition)) { equipment = default; return false; }
            if (row.Details.EnchantGrade > 0) { equipment = default; return false; } // enchant-grade multiplier not modeled yet.
            switch (definition)
            {
                case ArmorItemDefinition armor:
                    hardDefense += armor.Defense;
                    if (definition.Meta is { Refineable: true } armorMeta)
                        armorRefineDefense += (int)GeneratedRefineBonus.Bonus("Armor", (int)armorMeta.ArmorLevel, row.Refine);
                    break;
                case WeaponItemDefinition w when (row.Equip & 0x000002) != 0:
                    weapon = w;
                    if (definition.Meta is { Refineable: true })
                    {
                        var bonus = (int)GeneratedRefineBonus.Bonus("Weapon", w.WeaponLevel, row.Refine);
                        weaponRefineAtk += bonus;
                        if (w.WeaponType != WeaponType.Bow) weaponRefineMatk += bonus;
                    }
                    break;
                default:
                    equipment = default;
                    return false;
            }
            // Flat stat bonuses apply for every equipped row regardless of concrete type (pinned status_calc_pc_sub
            // sums param_bonus independently of the equip slot/item category).
            try { paramBonus += EquipmentBonusResolver.Resolve(definition.Meta?.Script, row.Refine); }
            catch (EquipmentBonusResolver.UnsupportedEquipScriptException) { equipment = default; return false; }
        }
        equipment = new EquipmentStats(hardDefense, weapon, paramBonus, weaponRefineAtk, weaponRefineMatk, armorRefineDefense);
        return true;
    }

    private static int CostForStep(int current) => current < 100 ? 2 + (current - 1) / 10 : 16 + 4 * ((current - 100) / 5);

    public uint StatIncreaseCost(ushort currentValue, int increaseAmount)
    {
        checked
        {
            uint total = 0;
            var value = (int)currentValue;
            for (var step = 0; step < increaseAmount; step++)
            {
                total += (uint)CostForStep(value);
                value++;
            }
            return total;
        }
    }

    private static ushort CurrentValue(CharacterGameplayState gameplay, CharacterBaseStat stat) => stat switch
    {
        CharacterBaseStat.Strength => gameplay.Strength,
        CharacterBaseStat.Agility => gameplay.Agility,
        CharacterBaseStat.Vitality => gameplay.Vitality,
        CharacterBaseStat.Intelligence => gameplay.Intelligence,
        CharacterBaseStat.Dexterity => gameplay.Dexterity,
        CharacterBaseStat.Luck => gameplay.Luck,
        _ => throw new ArgumentOutOfRangeException(nameof(stat), stat, "Unknown base stat."),
    };

    public StatIncreaseValidationResult ValidateStatIncrease(CharacterGameplayState gameplay, CharacterBaseStat stat, int increaseAmount)
    {
        if (!Enum.IsDefined(stat)) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.UnknownStat);
        if (increaseAmount <= 0) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.InvalidIncreaseAmount);

        var current = CurrentValue(gameplay, stat);
        var max = GeneratedProgressionRegistry.Get(gameplay.JobClass).MaxBaseStat;
        if (current >= max) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.MaxValueReached);

        var newValueLong = (long)current + increaseAmount;
        if (newValueLong > max) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.MaxValueReached);
        var newValueInt = (int)newValueLong;

        var cost = StatIncreaseCost(current, increaseAmount);
        if (cost > gameplay.StatPoints) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.InsufficientStatusPoints);

        var newValue = checked((ushort)newValueInt);
        var remaining = gameplay.StatPoints - cost;
        return StatIncreaseValidationResult.Valid(current, newValue, cost, remaining);
    }
}
