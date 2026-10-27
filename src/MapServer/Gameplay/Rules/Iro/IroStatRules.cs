using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Refine;
using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Gameplay.Rules.Iro;

// iRO gameplay rules. Independently maintained: this class must not inherit from, delegate to or share a
// concrete algorithm with its counterpart in the other region. Behavior currently reproduces the pinned rAthena
// (e985006171d2eb320ee512a653f4c83aea3d81b6) Renewal source exactly; official iRO behavior is NOT verified.
//
// Six stats: status_calc_pc_sub job bonus assembly (status.cpp:4249-4301). Derived stats: status_calc_misc
// (status.cpp:2600-2746) plus the status_calc_bl_main delta path (status.cpp:5964-6049) and RENEWAL MATK (6283-6361).
// Max HP/SP: status_calc_maxhp_pc/maxsp_pc (status.cpp:3511-3600), double table * (1 + stat*0.01) truncated.
// Equipment: each equipped armor row's DEF once, right-hand weapon. Allocation cost: PC_STATUS_POINT_COST.
// Fixtures VF-01/02/04/05/07/08/11/16 are the independent check. x1.25 upper/fourth multipliers, fourth-job traits and
// the Super Novice no-death bonus are NOT modeled (see JobSupport).
public sealed class IroStatRules : IStatRules
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

    // status.cpp:4291-4301: base + status + param_bonus + param_equip, each capped to 0..USHRT_MAX independently before any
    // derived stat reads it. EquipmentParamBonus already carries both halves (item scripts, combos, cards, random options).
    public PrimaryStats ApplyEquipmentBonus(PrimaryStats s, EquipmentParamBonus bonus) => new(
        Cap(s.Strength + bonus.Strength), Cap(s.Agility + bonus.Agility), Cap(s.Vitality + bonus.Vitality),
        Cap(s.Intelligence + bonus.Intelligence), Cap(s.Dexterity + bonus.Dexterity), Cap(s.Luck + bonus.Luck));

    private static ushort Cap(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    public DerivedStats CalculateDerivedStats(CharacterStatusSnapshot snapshot, EquipmentStats equipment = default)
    {
        var level = snapshot.State.BaseLevel;
        var b = ApplyEquipmentBonus(BasePrimaryStats(snapshot.State), equipment.ParamBonus);
        var e = snapshot.Effective;
        var baseDerived = BaseDerived(level, b, equipment.FlatBonusesOrEmpty);

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
        // Script bDef (pc_bonus SP_DEF1: status->def += val) lands in the same status->def before the refine term.
        var hardDef = Math.Clamp(equipment.HardDefense + equipment.FlatBonusesOrEmpty.Get("bDef") + (equipment.ArmorRefineDefense + 50) / 100, short.MinValue, ShortMax);

        // RENEWAL status_calc_bl_main MATK for BL_PC (status.cpp:6283-6361), NOT status_base_matk_min/max
        // alone (that function is only the StatusMATK term - 2559-2591). The live player formula is
        // StatusMATK + WeaponMATK +/- variance, then an overrefine widening:
        //   wMatk = rhw.matk, variance = rhw.matk * rhw.wlv / 10
        //   matk_min = StatusMATK + wMatk - variance, matk_max = StatusMATK + wMatk + variance
        //   if (overrefine > 0) { matk_min++; matk_max += overrefine - 1; }           (status.cpp:6325-6327)
        // rhw.matk itself is the weapon's static MagicAttack plus the refine bonus for non-bow weapons
        // (wa->matk += info->bonus/100, status.cpp:3973-3977) - exactly EquipmentStats.WeaponRefineMatk.
        // wlv is the weapon's level capped at MAX_WEAPON_LEVEL (5, status.cpp:3945-3946). overrefine is
        // EquipmentStats.WeaponOverrefine (wd->overrefine = info->randombonus_max/100, status.cpp:3986) -
        // despite the pinned YAML field's name "RandomBonus" this is a deterministic per-(weapon level,
        // refine level) value, not an RNG roll (see EquipmentStats's own doc comment). No left-hand/
        // dual-wield weapon is modeled in this project (see BasicAttackRangeResolver's own doc comment),
        // so only the right-hand (EquipmentStats.Weapon) term applies.
        var statusMatk = Math.Clamp(e.Intelligence + e.Intelligence / 2 + e.Dexterity / 5 + e.Luck / 3 + level / 4, 0, ushort.MaxValue);
        var matkMin = statusMatk;
        var matkMax = statusMatk;
        if (equipment.Weapon is { } weapon)
        {
            var wMatk = weapon.MagicAttack + equipment.WeaponRefineMatk;
            if (wMatk > 0)
            {
                var wlv = Math.Min(weapon.WeaponLevel, 5);
                var variance = wMatk * wlv / 10;
                matkMin += wMatk - variance;
                matkMax += wMatk + variance;
            }
            if (equipment.WeaponOverrefine > 0)
            {
                matkMin++;
                matkMax += equipment.WeaponOverrefine - 1;
            }
        }
        matkMin = Math.Clamp(matkMin, 0, ushort.MaxValue);
        matkMax = Math.Clamp(matkMax, 0, ushort.MaxValue);

        // CRI and perfect FLEE have no overlay from the supported statuses (LUK is unchanged by them).
        return baseDerived with { BaseAttack = batk, Hit = hit, Flee = flee, SoftDefense = def2, SoftMagicDefense = mdef2, HardDefense = hardDef, MagicAttackMin = matkMin, MagicAttackMax = matkMax };
    }

    // status_calc_misc, RENEWAL, BL_PC (status.cpp:2638-2745).
    // Equipment-script bonuses (pc_bonus SP_HIT/FLEE1/FLEE2/CRITICAL/DEF2/MDEF2, already x10 where pinned scales) are what
    // base_status holds when status_calc_misc adds the level/stat terms and caps (status.cpp:4405, 2600-2680).
    private static DerivedStats BaseDerived(int level, PrimaryStats s, EquipmentFlatBonuses bonus)
    {
        var hit = Math.Clamp(bonus.Get("bHit") + level + s.Dexterity + s.Luck / 3 + 175, 1, ShortMax);
        var flee = Math.Clamp(bonus.Get("bFlee") + level + s.Agility + s.Luck / 5 + 100, 1, ShortMax);
        var def2 = Math.Clamp(bonus.Get("bDef2") + (int)(((float)level + s.Vitality) / 2 + (float)s.Agility / 5), 0, ShortMax);
        var mdef2 = Math.Clamp(bonus.Get("bMdef2") + (int)(s.Intelligence + (float)level / 4 + (float)(s.Dexterity + s.Vitality) / 5), 0, ShortMax);
        var cri = Math.Clamp(bonus.Get("bCritical") + level / 10 + 10 + s.Luck * 3, 1, ShortMax);
        var flee2 = Math.Clamp(bonus.Get("bFlee2") + s.Luck + 10, 0, ShortMax);
        return new DerivedStats(BaseAtk(level, s.Strength, s.Dexterity, s.Luck), hit, flee, flee2, cri, def2, mdef2);
    }

    // status_base_atk, RENEWAL BL_PC non-ranged weapon, POW = 0 (status.cpp:2475-2477); clamp 0..USHRT_MAX.
    private static int BaseAtk(int level, int str, int dex, int luk) =>
        Math.Clamp((str * 10 + dex * 10 / 5 + luk * 10 / 3 + level * 10 / 4) / 10, 0, ushort.MaxValue);

    public uint MaxHp(CharacterGameplayState state, int effectiveVitality) =>
        ScaleResource(GeneratedProgressionRegistry.Get(state.JobClass).BaseHp[state.BaseLevel], effectiveVitality, TierMultiplied(state), 0, 0, 0, 0);

    public uint MaxSp(CharacterGameplayState state, int effectiveIntelligence) =>
        ScaleResource(GeneratedProgressionRegistry.Get(state.JobClass).BaseSp[state.BaseLevel], effectiveIntelligence, TierMultiplied(state), 0, 0, 0, 0);

    // Pinned status_calc_maxhp_pc / status_calc_maxsp_pc (status.cpp:3511-3590), in its order and precision:
    //   dmax = base[level]; dmax *= 1 + stat*0.01; x1.25 for JOBL_UPPER or a primary fourth job;
    //   + param_equip stat (item-script/combo VIT or INT: +1 each, cards excluded); + FIX (bMaxHP/SP + skills);
    //   + dmax * (hprate-100)/100 (sum of bMaxHPrate/SPrate, double); + (int64)(dmax * RATE/100) (statuses, SP skills); >= 1.
    // shortcut: the taekwon-ranker x3 (fame ranking), SU_POWEROFSEA's summoner-power +3000/+300 and every status-change
    // FIX/RATE term are not modeled (no fame ranking, summoner power count or those statuses exist yet).
    public (uint MaxHp, uint MaxSp) MaxResources(CharacterGameplayState state, MaxResourceContext context)
    {
        var equipment = context.Equipment;
        var stats = ApplyEquipmentBonus(BasePrimaryStats(state), equipment.ParamBonus);
        var job = GeneratedProgressionRegistry.Get(state.JobClass);
        var tier = TierMultiplied(state);
        int Skill(ushort id) => context.Skills?.Learned.FirstOrDefault(s => s.SkillId == id)?.Level ?? 0;
        int Ladder(int level, int perLevel, int atFive) => level > 0 ? perLevel * level + (level > 4 ? atFive : 0) : 0;

        // status_get_hpbonus FIX: bonus.hp, CR_TRUST 200/lv, SU_SPRITEMABLE 1000, SU_POWEROFSEA 1000, NV_BREAKTHROUGH and
        // NV_TRANSCENDENCE 350/lv (+250 from lv5).
        var hpFix = equipment.ParamBonus.MaxHp + 200 * Skill(248) + (Skill(5024) > 0 ? 1000 : 0) + (Skill(5037) > 0 ? 1000 : 0)
            + Ladder(Skill(5075), 350, 250) + Ladder(Skill(5077), 350, 250);
        // status_get_spbonus FIX: bonus.sp, SL_KAINA 30/lv, RA_RESEARCHTRAP 200+20/lv, WM_LESSON 30/lv, SU_SPRITEMABLE 100,
        // SU_POWEROFSEA 100, NV_BREAKTHROUGH and NV_TRANSCENDENCE 30/lv (+50 from lv5).
        var spFix = equipment.ParamBonus.MaxSp + 30 * Skill(466) + (Skill(2248) is var trap and > 0 ? 200 + 20 * trap : 0) + 30 * Skill(2412)
            + (Skill(5024) > 0 ? 100 : 0) + (Skill(5037) > 0 ? 100 : 0) + Ladder(Skill(5075), 30, 50) + Ladder(Skill(5077), 30, 50);
        // status_get_spbonus RATE (BL_PC part): HP_MEDITATIO lv, HW_SOULDRAIN 2/lv, RENEWAL BA_MUSICALLESSON (male) or
        // DC_DANCINGLESSON (female) lv; capped at -100.
        var spRate = Math.Max(-100, Skill(363) + 2 * Skill(364) + Skill(context.Sex != 0 ? (ushort)315 : (ushort)323));

        return (ScaleResource(job.BaseHp[state.BaseLevel], stats.Vitality, tier, equipment.ParamEquip.Vitality, hpFix, equipment.ParamBonus.MaxHpRate, 0),
                ScaleResource(job.BaseSp[state.BaseLevel], stats.Intelligence, tier, equipment.ParamEquip.Intelligence, spFix, equipment.ParamBonus.MaxSpRate, spRate));
    }

    // (sd.class_ & JOBL_UPPER) || pc_is_primary_fourth (pc.hpp:1740: Dragon Knight..Shadow Cross, Imperial Guard..Abyss
    // Chaser - the fourth jobs of the six first-class lines, FirstMask 1..6).
    private static bool TierMultiplied(CharacterGameplayState state) =>
        GeneratedJobEquipClass.TryGet((JobClass)state.JobClass, out var job) && ((job.Tier & 1) != 0 || ((job.Tier & 8) != 0 && job.FirstMask is >= 1 and <= 6));

    private static uint ScaleResource(uint table, int stat, bool tierMultiplied, int paramEquip, int fix, int equipRate, int statusRate)
    {
        double max = table;
        if (stat > 0) max *= 1.0 + stat * 0.01;
        if (tierMultiplied) max *= 1.25;
        max += paramEquip;
        max += fix;
        max += max * equipRate / 100;
        max += (long)(max * statusRate / 100);
        return max < 1.0 ? 1u : (uint)Math.Min(max, uint.MaxValue);
    }

    // pc_statusup / equipment changes -> status_calc_pc (SCO_NONE): maxima are rebuilt and the CURRENT values are only
    // clamped down to the new maxima - raising a maximum never heals (fixture VF-11).
    public CharacterGameplayState WithRecalculatedResources(CharacterGameplayState state, MaxResourceContext context = default)
    {
        var (maxHp, maxSp) = MaxResources(state, context);
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
    //
    // A4: a KNOWN, modeled item category this method does not yet compute a stat contribution for
    // (left-hand/dual-wield weapon ATK, ammo ATK, shadow gear refine) is recognized and SKIPPED - it
    // contributes zero, exactly like an unequipped slot would - rather than failing resolution for the
    // WHOLE character. Only a genuinely UNRECOGNIZED item (not in `items` at all, or an enchant-graded
    // item whose multiplier is unmodeled) makes TryResolveEquipment return false, which is the only
    // case that should ever degrade an otherwise-valid player's entire equipment contribution to
    // EquipmentStats.default (see TryGetCombatSnapshotAsync's own doc comment on that fallback). Before
    // this distinction, equipping a single ammo/shadow-gear/left-hand-weapon row - all routine, common
    // gameplay states reachable through CharacterEquipmentMutationService's existing equip paths -
    // silently discarded every OTHER equipped item's DEF/refine/param-bonus contribution too.
    public bool TryResolveEquipment(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items, out EquipmentStats equipment, EquipmentCharacter? character = null,
        Func<ItemDefinition, bool>? noEquip = null)
    {
        var hardDefense = 0;
        WeaponItemDefinition? weapon = null;
        var weaponRefineAtk = 0;
        var weaponRefineMatk = 0;
        var armorRefineDefense = 0;
        var weaponOverrefine = 0;
        // Combined id pool for ComboResolver.ActiveCombos (pinned pc_checkcombo, pc.cpp:11803-11900): each
        // equipped row's own ItemId once, plus every nonzero Card0-3 - cards are real, common combo members.
        var presentItemIds = new List<uint>();
        // Pinned status_calc_pc walks sd->equip_index[] in EQI order (status.cpp:3905), each row once however many slot bits it
        // carries; only "last script wins" parameters (bAtkEle, bDefEle, ...) can observe the order.
        var equipped = inventory.Items.Where(row => row.Equip != 0).OrderBy(row => EquipSlots.EqiOrder(row.Equip)).ToList();
        foreach (var row in equipped)
        {
            if (!items.TryGetValue(row.ItemId, out var definition)) { equipment = default; return false; }
            // EnchantGrade is already the pinned 0-indexed e_enchantgrade value this project's own
            // ItemGrantResolver assigns (0=None..4=A, matching MaxEnchantGrade=4/ENCHANTGRADE_A) - never the
            // pinned wire byte's own 1-indexed scheme (findCurrentLevelInfo's `item.enchantgrade - 1` only
            // exists because THAT byte is 1-indexed with 0 meaning "never graded"; this domain model has no
            // separate "ungraded" sentinel distinct from grade None, matching how ItemGrantResolver rolls it).
            if (row.Details.EnchantGrade >= GeneratedEnchantGradeBonus.Grades.Length) { equipment = default; return false; } // beyond ENCHANTGRADE_A - data/generation invariant violation, not a legitimate state.
            var enchantGradeName = GeneratedEnchantGradeBonus.Grades[row.Details.EnchantGrade];
            switch (definition)
            {
                case ArmorItemDefinition armor:
                    hardDefense += armor.Defense;
                    // Pinned status.cpp:4012-4013: armor's own refinedef term never receives an
                    // enchant-grade multiplier at all (only the weapon ATK2/MATK terms below do,
                    // status.cpp:3961,3980) - this is the pinned source's own asymmetry, not an omission here.
                    if (definition.Meta is { Refineable: true } armorMeta)
                        armorRefineDefense += (int)GeneratedRefineBonus.Bonus("Armor", (int)armorMeta.ArmorLevel, row.Refine);
                    break;
                case WeaponItemDefinition w when (row.Equip & 0x000002) != 0:
                    weapon = w;
                    if (definition.Meta is { Refineable: true })
                    {
                        // Pinned status.cpp:3957,3977: `wa->atk2 += info->bonus/100` and (non-bow)
                        // `wa->matk += info->bonus/100` - GeneratedRefineBonus.Bonus is the RAW
                        // pinned value (e.g. 1000 for a level-1 weapon at +5), divided by 100 BEFORE
                        // accumulating, never accumulated raw.
                        var bonus = (int)GeneratedRefineBonus.Bonus("Weapon", w.WeaponLevel, row.Refine) / 100;
                        // Pinned status.cpp:3961,3980: `wa->atk2 += ((info->bonus/100) * enchantgrade_info->bonus) / 100`
                        // - the grade multiplier is computed from the SAME already-/100-scaled refine bonus term
                        // above, then divided by 100 again, and ADDED on top (not multiplied into) the plain
                        // refine bonus. GeneratedEnchantGradeBonus.Bonus returns 0 for an ungraded weapon or a
                        // (type,level) the real data never tabulates (every weapon level except 5), matching the
                        // pinned enchantgrade_info == nullptr case exactly (no term added).
                        var gradeBonus = (int)GeneratedEnchantGradeBonus.Bonus("Weapon", w.WeaponLevel, enchantGradeName) * bonus / 100;
                        weaponRefineAtk += bonus + gradeBonus;
                        if (w.WeaponType != WeaponType.Bow) weaponRefineMatk += bonus + gradeBonus;
                        // Pinned status.cpp:3986: wd->overrefine = info->randombonus_max/100 - a single flat value
                        // for the right-hand weapon (not accumulated/summed like atk2/matk across rows, since there
                        // is only ever one right-hand weapon).
                        weaponOverrefine = (int)GeneratedRefineBonus.OverrefineBonus("Weapon", w.WeaponLevel, row.Refine) / 100;
                    }
                    break;
                // A left-hand weapon (dual-wield, reachable via CharacterEquipmentMutationService's own EQP_ARMS
                // logic) is a real, currently-equippable state this project does not model the combat
                // contribution of (see BasicAttackRangeResolver's own "no left-hand/dual-wield weapon modeled"
                // doc comment) - but it must not make every OTHER equipped item's contribution vanish. Recognized
                // and skipped, not rejected.
                case WeaponItemDefinition:
                    break;
                // Shadow gear's Defense is modeled data (feeds hard DEF exactly like armor, per its own doc
                // comment) - only its refine bonus is out of scope (RefineDataCompiler only generates the Weapon
                // and Armor groups; see this method's own doc comment).
                case ShadowGearItemDefinition shadow:
                    hardDefense += shadow.Defense;
                    break;
                // Ammo (arrows/bullets/kunai/...) is routine, common equipment for several classes - its own
                // ATK contribution belongs to the live combat formula (Feature 17), not this general player
                // status calculation, but an equipped ammo row must not invalidate every other item's
                // contribution either. Recognized and skipped, not rejected.
                case AmmoItemDefinition:
                    break;
                default:
                    equipment = default;
                    return false;
            }
            presentItemIds.Add((uint)row.ItemId);
            var details = row.Details;
            if (IsSpecialCard0(details.Card0) || definition is AmmoItemDefinition) continue;
            foreach (var cardId in (ReadOnlySpan<uint>)[details.Card0, details.Card1, details.Card2, details.Card3])
            {
                if (cardId == 0) continue;
                if (!items.TryGetValue((int)cardId, out var cardDefinition)) { equipment = default; return false; }
                if (cardDefinition is not CardItemDefinition) { equipment = default; return false; } // a non-card id in a card slot is a data/generation invariant violation.
                presentItemIds.Add(cardId);
            }
            foreach (var option in (ReadOnlySpan<InventoryOption>)[details.Option0, details.Option1, details.Option2, details.Option3, details.Option4])
                if (option.Id != 0 && !GeneratedItems.RandomOptions.ContainsKey((uint)option.Id)) { equipment = default; return false; } // unknown random-option id - data/generation invariant violation.
        }

        // Script passes in pinned status_calc_pc order (status.cpp:3905-4185): every item's own Script, the ammo Script under
        // LR_FLAG_ARROW, every active combo, every card, every random option. One accumulator applies each pc_bonus case's own
        // pinned rule (EquipmentBonusParameters); an unsupported script anywhere rejects the whole equipment set, never a
        // silently partial bonus.
        var bonuses = new EquipmentBonusAccumulator();
        var paramEquip = EquipmentParamBonus.None;
        // Pinned pc_checkequip(sd, equip_bitmask[eqi]) for the getequip* builtins: the worn row on that EQI slot.
        var slotMasks = EquipSlots.Bitmask.Values.ToArray();
        CharacterInventoryItem? EquippedAt(int eqi) => eqi < slotMasks.Length ? equipped.FirstOrDefault(r => (r.Equip & slotMasks[eqi]) != 0) : null;
        EquipmentScriptContext Context(CharacterInventoryItem row, EquipmentBonusHand hand) =>
            new(row.Refine, row.Details.EnchantGrade, default, hand, EquippedAt, items, character, row);
        try
        {
            foreach (var row in equipped)
            {
                var definition = items[row.ItemId];
                if (definition is AmmoItemDefinition) continue;
                // A left-hand WEAPON's own script runs as LR_FLAG_WEAPON (status.cpp:3948-3994).
                var hand = definition is WeaponItemDefinition && row.Equip == EquipSlots.Bitmask[EquipSlot.HandLeft] ? EquipmentBonusHand.Left : EquipmentBonusHand.Right;
                // Pinned itemdb_isNoEquip on this map (allow_equip_restricted_item: yes): worn, but its script gives no effect.
                if (noEquip?.Invoke(definition) != true) EquipmentBonusResolver.Accumulate(definition.Meta?.Script, Context(row, hand), bonuses);
            }
            // shortcut: pinned skips the ammo script for IG_THROWABLE items (status.cpp:4046); item groups are not
            // generated yet - upgrade when item_group_db is.
            foreach (var row in equipped)
                if (items[row.ItemId] is AmmoItemDefinition ammo && noEquip?.Invoke(ammo) != true)
                    EquipmentBonusResolver.Accumulate(ammo.Meta?.Script, Context(row, EquipmentBonusHand.Arrow), bonuses);
            // S7 combos (pinned pc_checkcombo, pc.cpp:11803-11900): every combo whose activation set (equip items and/or cards,
            // see ComboResolver) is satisfied runs its shared script with current_equip_item_index = -1 (status.cpp:4060).
            // A combo with any restricted member runs no script on this map (status.cpp:4068-4082).
            foreach (var combo in ComboResolver.ActiveCombos(ItemCatalog.Current.Combos, presentItemIds))
                if (noEquip is null || combo.Alternatives.Any(set => ComboResolver.IsSatisfied(set, presentItemIds) && !set.Any(id => items.TryGetValue((int)id, out var member) && noEquip(member))))
                    EquipmentBonusResolver.Accumulate(combo.Script, new EquipmentScriptContext(EquippedAt: EquippedAt, Items: items, Character: character), bonuses);
            // Pinned memcpy(param_equip, param_bonus) right after the combo pass (status.cpp:4083): VIT/INT from item scripts,
            // ammo and combos - not cards or random options - also add +1 max HP/SP each (status_calc_maxhp_pc).
            paramEquip = bonuses.Param;
            // Cards run with the CARRYING row as current_equip_item_index (status.cpp:4100), so getrefine() inside a card
            // reads that row's refine; the row worn exactly at EQP_HAND_L runs its cards and options as LR_FLAG_WEAPON.
            foreach (var row in equipped)
            {
                if (items[row.ItemId] is AmmoItemDefinition || IsSpecialCard0(row.Details.Card0)) continue;
                var d = row.Details;
                foreach (var cardId in (ReadOnlySpan<uint>)[d.Card0, d.Card1, d.Card2, d.Card3])
                    if (cardId != 0 && noEquip?.Invoke(items[(int)cardId]) != true) EquipmentBonusResolver.Accumulate(items[(int)cardId].Meta?.Script, Context(row, LeftHandOf(row)), bonuses);
            }
            foreach (var row in equipped)
            {
                if (items[row.ItemId] is AmmoItemDefinition || noEquip?.Invoke(items[row.ItemId]) == true) continue;
                var d = row.Details;
                foreach (var option in (ReadOnlySpan<InventoryOption>)[d.Option0, d.Option1, d.Option2, d.Option3, d.Option4])
                    if (option.Id != 0) EquipmentBonusResolver.Accumulate(GeneratedItems.RandomOptions[(uint)option.Id], Context(row, LeftHandOf(row)) with { Option = option }, bonuses);
            }
        }
        catch (EquipmentBonusResolver.UnsupportedEquipScriptException) { equipment = default; return false; }

        equipment = new EquipmentStats(hardDefense, weapon, bonuses.Param, weaponRefineAtk, weaponRefineMatk, armorRefineDefense, weaponOverrefine,
            bonuses.Extended, bonuses.Triggered, bonuses.Flat, bonuses.Multi, bonuses.ClientEffects, paramEquip);
        return true;
    }

    private static EquipmentBonusHand LeftHandOf(CharacterInventoryItem row) =>
        row.Equip == EquipSlots.Bitmask[EquipSlot.HandLeft] ? EquipmentBonusHand.Left : EquipmentBonusHand.Right;

    // Pinned itemdb_isspecial (itemdb.hpp:33-38): CARD0_FORGE/CREATE/PET mark a forged, brewed or pet-egg row whose card
    // slots carry a maker signature, not cards; status_calc_pc skips their card scripts (status.cpp:4112).
    private static bool IsSpecialCard0(uint card0) => card0 is 0x00FF or 0x00FE or 0x0100;

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
