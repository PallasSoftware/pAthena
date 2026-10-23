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

    // status.cpp:4291-4301: base + status + param_bonus (equip flat bonuses) + param_equip, each capped to 0..USHRT_MAX
    // independently before any derived stat reads it. param_equip (bonus2-5/card/combo territory) is not modeled yet.
    public PrimaryStats ApplyEquipmentBonus(PrimaryStats s, EquipmentParamBonus bonus) => new(
        Cap(s.Strength + bonus.Strength), Cap(s.Agility + bonus.Agility), Cap(s.Vitality + bonus.Vitality),
        Cap(s.Intelligence + bonus.Intelligence), Cap(s.Dexterity + bonus.Dexterity), Cap(s.Luck + bonus.Luck));

    private static ushort Cap(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    public DerivedStats CalculateDerivedStats(CharacterStatusSnapshot snapshot, EquipmentStats equipment = default)
    {
        var level = snapshot.State.BaseLevel;
        var b = ApplyEquipmentBonus(BasePrimaryStats(snapshot.State), equipment.ParamBonus);
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
    public bool TryResolveEquipment(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items, out EquipmentStats equipment)
    {
        var hardDefense = 0;
        WeaponItemDefinition? weapon = null;
        var paramBonus = EquipmentParamBonus.None;
        var weaponRefineAtk = 0;
        var weaponRefineMatk = 0;
        var armorRefineDefense = 0;
        var weaponOverrefine = 0;
        var extendedBonuses = new List<EquipmentExtendedBonus>();
        var triggeredBonuses = new List<TriggeredBonusDefinition>();
        // Combined id pool for ComboResolver.ActiveCombos (pinned pc_checkcombo, pc.cpp:11803-11900): each
        // equipped row's own ItemId once, plus every nonzero Card0-3 - cards are real, common combo members.
        var presentItemIds = new List<uint>();
        foreach (var row in inventory.Items)
        {
            if (row.Equip == 0) continue; // One inventory row counts once however many slot bits it carries.
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
            // Flat stat bonuses apply for every equipped row regardless of concrete type (pinned status_calc_pc_sub
            // sums param_bonus independently of the equip slot/item category).
            try
            {
                paramBonus += EquipmentBonusResolver.Resolve(definition.Meta?.Script, row.Refine, default, out var ownExtended, out var ownTriggered);
                extendedBonuses.AddRange(ownExtended);
                triggeredBonuses.AddRange(ownTriggered);
            }
            catch (EquipmentBonusResolver.UnsupportedEquipScriptException) { equipment = default; return false; }
            presentItemIds.Add((uint)row.ItemId);

            // Pinned pc_bonus's own card loop (pc.cpp run_script over each card[i]): a card's bonus script runs
            // exactly like the equip's own Script, summed into the SAME param_bonus accumulator - cards are not a
            // separate bonus category. getrefine() inside a CARD's own script reads the CARD's own refine (always
            // 0 - cards have no Refine field of their own), never the equip row's refine (script.cpp's
            // current_equip_item_index always means "whichever item's own script is currently executing").
            var details = row.Details;
            foreach (var cardId in (ReadOnlySpan<uint>)[details.Card0, details.Card1, details.Card2, details.Card3])
            {
                if (cardId == 0) continue;
                if (!items.TryGetValue((int)cardId, out var cardDefinition)) { equipment = default; return false; }
                if (cardDefinition is not CardItemDefinition) { equipment = default; return false; } // a non-card id in a card slot is a data/generation invariant violation.
                try
                {
                    paramBonus += EquipmentBonusResolver.Resolve(cardDefinition.Meta?.Script, 0, default, out var cardExtended, out var cardTriggered);
                    extendedBonuses.AddRange(cardExtended);
                    triggeredBonuses.AddRange(cardTriggered);
                }
                catch (EquipmentBonusResolver.UnsupportedEquipScriptException) { equipment = default; return false; }
                presentItemIds.Add(cardId);
            }

            // Pinned status_calc_pc_additional's own option loop (status.cpp, over sd->inventory.u.items_inventory[i].option[0..MAX_ITEM_RDM_OPT))
            // runs each OCCUPIED random-option slot's own item_randomopt_db.yml script, current_equip_opt_index
            // bound to that same slot (so getrandomoptinfo() inside it reads THIS slot's own Id/Value/Param,
            // never another slot's). option.Id == 0 means the slot is empty (never persisted with a real option
            // id) - skipped exactly like a zero card slot, not resolved against option id 0 (which does not exist
            // in item_randomopt_db.yml).
            foreach (var option in (ReadOnlySpan<InventoryOption>)[details.Option0, details.Option1, details.Option2, details.Option3, details.Option4])
            {
                if (option.Id == 0) continue;
                if (!GeneratedItems.RandomOptions.TryGetValue((uint)option.Id, out var optionScript)) { equipment = default; return false; } // unknown random-option id - data/generation invariant violation.
                try
                {
                    paramBonus += EquipmentBonusResolver.Resolve(optionScript, row.Refine, option, out var optionExtended, out var optionTriggered);
                    extendedBonuses.AddRange(optionExtended);
                    triggeredBonuses.AddRange(optionTriggered);
                }
                catch (EquipmentBonusResolver.UnsupportedEquipScriptException) { equipment = default; return false; }
            }
        }

        // S7 combos (pinned pc_checkcombo, pc.cpp:11803-11900): every combo whose activation set (equip items
        // and/or cards, see ComboResolver's own doc comment) is currently satisfied runs its own shared script
        // exactly like an equip/card script, summed into the SAME param_bonus/extendedBonuses accumulators - a
        // combo is not a separate bonus category either. An unsupported combo script rejects the WHOLE equip,
        // same fail-clearly convention as every other script source here (never silently skip a combo's bonus).
        foreach (var combo in ComboResolver.ActiveCombos(ItemCatalog.Current.Combos, presentItemIds))
        {
            try
            {
                paramBonus += EquipmentBonusResolver.Resolve(combo.Script, 0, default, out var comboExtended, out var comboTriggered);
                extendedBonuses.AddRange(comboExtended);
                triggeredBonuses.AddRange(comboTriggered);
            }
            catch (EquipmentBonusResolver.UnsupportedEquipScriptException) { equipment = default; return false; }
        }
        equipment = new EquipmentStats(hardDefense, weapon, paramBonus, weaponRefineAtk, weaponRefineMatk, armorRefineDefense, weaponOverrefine, extendedBonuses, triggeredBonuses);
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
