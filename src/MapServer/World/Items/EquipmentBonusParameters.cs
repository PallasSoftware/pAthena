namespace Athena.Net.MapServer.World.Items;

// Pinned sd->state.lr_flag while an equipment script runs (status_calc_pc, status.cpp:3906-4185): a left-hand weapon's own
// script and the cards of the row worn exactly at EQP_HAND_L run as LR_FLAG_WEAPON, the ammo script as LR_FLAG_ARROW,
// everything else as LR_FLAG_NONE. (LR_FLAG_SHIELD only feeds Royal Guard's shield MDEF, sd->bonus.shieldmdef, which is not
// modeled; a shield's own script therefore runs as Right here.)
public enum EquipmentBonusHand : byte { Right, Left, Arrow }

// Who reads an accumulated parameter. LiveDerivedStats: CalculateDerivedStats applies it today. The Feature 17/18 owners
// receive correctly-accumulated, typed values through EquipmentStats.FlatBonuses and apply them when their formulas land;
// Unconsumed: Feature 16 accumulates it, but the system that reads it (weight, regen, drops, ...) does not exist yet.
public enum EquipmentBonusConsumer : byte { LiveDerivedStats, Feature17Combat, Feature18Skill, Unconsumed }

// How one pc_bonus case folds a value into its accumulator (all accumulators start at 0: status.cpp:3891 memsets sd->bonus).
public enum EquipmentBonusCombine : byte
{
    Add,      // acc += val * Scale, clamped after every step when the pinned case caps (cap_value)
    Max,      // acc = max(acc, val)
    Min,      // acc = min(acc, val * Scale)
    Set,      // acc = val (the last script wins)
    OrShift,  // acc |= 1 << val (element/race/class bit sets)
    OrValue,  // acc |= val
    OrMask,   // acc |= Mask (the value is ignored)
    Flag,     // acc = 1
}

public enum EquipmentBonusHandMode : byte
{
    Shared,    // the same accumulator for every lr_flag except the ones listed separately
    Separate,  // a distinct accumulator per hand (pinned rhw/lhw or arrow_* fields)
    Ignored,   // the pinned case does nothing for this lr_flag
}

public sealed record EquipmentBonusParameter(
    EquipmentBonusCombine Combine, EquipmentBonusConsumer Consumer, int Scale = 1,
    EquipmentBonusHandMode Left = EquipmentBonusHandMode.Shared, EquipmentBonusHandMode Arrow = EquipmentBonusHandMode.Ignored,
    int ClampMin = int.MinValue, int ClampMax = int.MaxValue, uint Mask = 0);

// Every single-value pc_bonus case of the pinned source (pc.cpp:3725-4488), keyed by its script constant name
// (script_constants.hpp export_constant2("bName", SP_*)). The primary-stat family (bStr..bLuk, bAllStats, bAgiVit,
// bAgiDexStr, bMaxHP/SP[rate], bHP/SPrecovRate) is folded into EquipmentParamBonus instead (EquipmentBonusResolver).
// A name absent from both is not a pc_bonus case at all (pinned: "unknown bonus type" warning) and stays unsupported.
public static class EquipmentBonusParameters
{
    private const int ShortMin = short.MinValue, ShortMax = short.MaxValue;
    private const EquipmentBonusHandMode Sep = EquipmentBonusHandMode.Separate, Ign = EquipmentBonusHandMode.Ignored, Shr = EquipmentBonusHandMode.Shared;
    private const EquipmentBonusConsumer Live = EquipmentBonusConsumer.LiveDerivedStats, F17 = EquipmentBonusConsumer.Feature17Combat,
        F18 = EquipmentBonusConsumer.Feature18Skill, Unconsumed = EquipmentBonusConsumer.Unconsumed;

    // Pinned EQP_* masks the unbreakable/unstripable cases OR in (mmo.hpp:337-358, pc.hpp:1140-1145).
    private const uint EqpWeapon = 0x2, EqpArmor = 0x10, EqpHelm = 0x1 | 0x100 | 0x200, EqpShield = 0x20, EqpGarment = 0x4, EqpShoes = 0x40;

    private static EquipmentBonusParameter Add(EquipmentBonusConsumer consumer, int scale = 1, bool shortCap = false,
        EquipmentBonusHandMode left = Shr, EquipmentBonusHandMode arrow = Ign) =>
        new(EquipmentBonusCombine.Add, consumer, scale, left, arrow, shortCap ? ShortMin : int.MinValue, shortCap ? ShortMax : int.MaxValue);

    private static EquipmentBonusParameter Of(EquipmentBonusCombine combine, EquipmentBonusConsumer consumer, int scale = 1, uint mask = 0,
        EquipmentBonusHandMode left = Shr, EquipmentBonusHandMode arrow = Ign) => new(combine, consumer, scale, left, arrow, Mask: mask);

    // Pinned script symbols are case-insensitive (script.cpp search_str: strcasecmp, calc_hash: TOLOWER) - item data spells
    // bPAtk where the constant is exported as bPatk - so lookups ignore case and TryGet hands back the canonical key.
    public static bool TryGet(string name, out string canonical, out EquipmentBonusParameter parameter)
    {
        canonical = Canonical.GetValueOrDefault(name, name);
        return ByName.TryGetValue(name, out parameter!);
    }

    private static readonly Dictionary<string, string> Canonical;

    static EquipmentBonusParameters() => Canonical = ByName.Keys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, EquipmentBonusParameter> ByName = new Dictionary<string, EquipmentBonusParameter>(StringComparer.OrdinalIgnoreCase)
    {
        // Trait stats (SP_POW..SP_CRT, SP_ALL_TRAIT_STATS -> param_bonus[PARAM_POW..]): 4th-job traits are not modeled.
        ["bPow"] = Add(Unconsumed), ["bSta"] = Add(Unconsumed), ["bWis"] = Add(Unconsumed),
        ["bSpl"] = Add(Unconsumed), ["bCon"] = Add(Unconsumed), ["bCrt"] = Add(Unconsumed), ["bAllTraitStats"] = Add(Unconsumed),

        // SP_ATK1/SP_ATK2: rhw/lhw atk, capped 0..USHRT_MAX, nothing for arrows.
        ["bAtk"] = new(EquipmentBonusCombine.Add, F17, 1, Sep, Ign, 0, ushort.MaxValue),
        ["bAtk2"] = new(EquipmentBonusCombine.Add, F17, 1, Sep, Ign, 0, ushort.MaxValue),
        ["bBaseAtk"] = Add(F17, shortCap: true),                 // RENEWAL sd->bonus.eatk
        ["bDef"] = Add(Live, shortCap: true),                    // status->def (hard DEF)
        ["bDef2"] = Add(Live, shortCap: true),                   // status->def2 (soft DEF)
        ["bMdef"] = Add(F17, shortCap: true),                    // status->mdef (hard MDEF: no DerivedStats field)
        ["bMdef2"] = Add(Live, shortCap: true),                  // status->mdef2 (soft MDEF)
        ["bHit"] = Add(Live, shortCap: true, arrow: Sep),        // status->hit / sd->bonus.arrow_hit
        ["bFlee"] = Add(Live, shortCap: true),                   // status->flee
        ["bFlee2"] = Add(Live, scale: 10, shortCap: true),       // status->flee2 += val*10
        ["bCritical"] = Add(Live, scale: 10, shortCap: true, arrow: Sep), // status->cri / arrow_cri += val*10
        ["bPatk"] = Add(F17, shortCap: true), ["bSmatk"] = Add(F17, shortCap: true), ["bRes"] = Add(F17, shortCap: true),
        ["bMres"] = Add(F17, shortCap: true), ["bHplus"] = Add(F18, shortCap: true), ["bCrate"] = Add(F17, shortCap: true),
        ["bAtkEle"] = Of(EquipmentBonusCombine.Set, F17, left: Sep, arrow: Sep), // rhw.ele / lhw.ele / arrow_ele (or rhw.ele for ranged weapons)
        ["bDefEle"] = Of(EquipmentBonusCombine.Set, F17),       // status->def_ele
        ["bMaxAP"] = Add(Unconsumed), ["bMaxAPrate"] = Add(Unconsumed),
        ["bUseSPrate"] = Add(F18),                               // sd->dsprate
        ["bAtkRange"] = Add(F17, left: Sep, arrow: Sep),         // rhw/lhw.range (arrow: only for ranged weapons)
        ["bSpeedRate"] = Of(EquipmentBonusCombine.Min, Unconsumed, scale: -1), // speed_rate = min(speed_rate, -val)
        ["bSpeedAddRate"] = Add(Unconsumed, scale: -1),          // speed_add_rate -= val
        ["bAspd"] = Add(F17, scale: -10),                        // aspd_add -= 10*val
        ["bAspdRate"] = Add(F17),                                // RENEWAL_ASPD aspd_rate2 += val
        ["bCriticalDef"] = Add(F17), ["bNearAtkDef"] = Add(F17), ["bLongAtkDef"] = Add(F17),
        ["bDoubleRate"] = Of(EquipmentBonusCombine.Max, F17, left: Ign), // LR_FLAG_NONE only
        ["bDoubleAddRate"] = Add(F17, left: Ign),
        ["bMatkRate"] = Add(F17),                                // sd->matk_rate (applied to base MATK, status.cpp:4411)
        ["bIgnoreDefEle"] = Of(EquipmentBonusCombine.OrShift, F17, left: Sep),
        ["bIgnoreDefRace"] = Of(EquipmentBonusCombine.OrShift, F17, left: Sep),
        ["bIgnoreDefClass"] = Of(EquipmentBonusCombine.OrShift, F17, left: Sep),
        ["bAtkRate"] = Add(F17),                                 // RENEWAL sd->bonus.atk_rate
        ["bMagicAtkDef"] = Add(F17), ["bMiscAtkDef"] = Add(F17),
        ["bIgnoreMdefEle"] = Of(EquipmentBonusCombine.OrShift, F17),
        ["bIgnoreMdefRace"] = Of(EquipmentBonusCombine.OrShift, F17),
        ["bPerfectHitRate"] = Of(EquipmentBonusCombine.Max, F17),
        ["bPerfectHitAddRate"] = Add(F17),
        ["bCriticalRate"] = Add(F17),
        ["bDefRatioAtkEle"] = Of(EquipmentBonusCombine.OrShift, F17, left: Sep),
        ["bDefRatioAtkRace"] = Of(EquipmentBonusCombine.OrShift, F17, left: Sep),
        ["bDefRatioAtkClass"] = Of(EquipmentBonusCombine.OrShift, F17, left: Sep),
        // sd->hit_rate .. crate_rate start at 100 in pinned (status.cpp:3790-3793); the accumulator holds the delta.
        ["bHitRate"] = Add(F17), ["bFleeRate"] = Add(F17), ["bFlee2Rate"] = Add(F17), ["bDefRate"] = Add(F17), ["bDef2Rate"] = Add(F17),
        ["bMdefRate"] = Add(F17), ["bMdef2Rate"] = Add(F17), ["bPAtkRate"] = Add(F17), ["bSMatkRate"] = Add(F17), ["bResRate"] = Add(F17),
        ["bMResRate"] = Add(F17), ["bHPlusRate"] = Add(F18), ["bCRateRate"] = Add(F17),
        ["bRestartFullRecover"] = Of(EquipmentBonusCombine.Flag, Unconsumed),
        ["bNoCastCancel"] = Of(EquipmentBonusCombine.Flag, F18), ["bNoCastCancel2"] = Of(EquipmentBonusCombine.Flag, F18),
        ["bNoSizeFix"] = Of(EquipmentBonusCombine.Flag, F17),
        ["bNoMagicDamage"] = new(EquipmentBonusCombine.Add, F17, 1, Shr, Ign, 0, 100),
        ["bNoWeaponDamage"] = new(EquipmentBonusCombine.Add, F17, 1, Shr, Ign, 0, 100),
        ["bNoMiscDamage"] = new(EquipmentBonusCombine.Add, F17, 1, Shr, Ign, 0, 100),
        ["bNoGemStone"] = Of(EquipmentBonusCombine.Flag, F18),
        ["bIntravision"] = Of(EquipmentBonusCombine.Flag, Unconsumed),
        ["bNoKnockback"] = Of(EquipmentBonusCombine.Flag, F17),
        ["bSplashRange"] = Of(EquipmentBonusCombine.Max, F17, arrow: Shr),     // no lr_flag check in pinned
        ["bSplashAddRange"] = Add(F17, arrow: Shr),
        ["bShortWeaponDamageReturn"] = Add(F17), ["bLongWeaponDamageReturn"] = Add(F17),
        ["bMagicDamageReturn"] = Add(F17), ["bReduceDamageReturn"] = Add(F17),
        ["bPerfectHide"] = Of(EquipmentBonusCombine.Flag, F18),
        ["bUnbreakable"] = Add(F17),
        ["bUnbreakableWeapon"] = Of(EquipmentBonusCombine.OrMask, F17, mask: EqpWeapon),
        ["bUnbreakableArmor"] = Of(EquipmentBonusCombine.OrMask, F17, mask: EqpArmor),
        ["bUnbreakableHelm"] = Of(EquipmentBonusCombine.OrMask, F17, mask: EqpHelm),
        ["bUnbreakableShield"] = Of(EquipmentBonusCombine.OrMask, F17, mask: EqpShield),
        ["bUnbreakableGarment"] = Of(EquipmentBonusCombine.OrMask, F17, mask: EqpGarment),
        ["bUnbreakableShoes"] = Of(EquipmentBonusCombine.OrMask, F17, mask: EqpShoes),
        ["bClassChange"] = Of(EquipmentBonusCombine.Set, F17),
        ["bSkillRatio"] = Add(F18),
        ["bShortAtkRate"] = Add(F17), ["bLongAtkRate"] = Add(F17), ["bNonCritAtkRate"] = Add(F17),
        ["bBreakWeaponRate"] = Add(F17), ["bBreakArmorRate"] = Add(F17), ["bAddStealRate"] = Add(F18),
        ["bDelayrate"] = Add(F18, scale: -1),                    // sd->bonus.delayrate -= val
        ["bCritAtkRate"] = Add(F17), ["bCritDefRate"] = Add(F17),
        ["bNoRegen"] = Of(EquipmentBonusCombine.OrValue, Unconsumed),
        ["bUnstripableWeapon"] = Of(EquipmentBonusCombine.OrMask, F18, mask: EqpWeapon),
        ["bUnstripable"] = Of(EquipmentBonusCombine.OrMask, F18, mask: EqpArmor),
        ["bUnstripableArmor"] = Of(EquipmentBonusCombine.OrMask, F18, mask: EqpArmor),
        ["bUnstripableHelm"] = Of(EquipmentBonusCombine.OrMask, F18, mask: EqpHelm),
        ["bUnstripableShield"] = Of(EquipmentBonusCombine.OrMask, F18, mask: EqpShield),
        ["bHPDrainValue"] = Add(F17, left: Sep), ["bSPDrainValue"] = Add(F17, left: Sep),  // [CLASS_NORMAL] and [CLASS_BOSS]
        ["bSPGainValue"] = Add(F17, left: Ign), ["bHPGainValue"] = Add(F17, left: Ign),    // LR_FLAG_NONE only
        ["bLongSPGainValue"] = Add(F17, left: Ign), ["bLongHPGainValue"] = Add(F17, left: Ign),
        ["bMagicSPGainValue"] = Add(F17, left: Ign), ["bMagicHPGainValue"] = Add(F17, left: Ign),
        ["bHealPower"] = Add(F18), ["bHealPower2"] = Add(F18),   // SP_ADD_HEAL_RATE / SP_ADD_HEAL2_RATE
        ["bAddItemHealRate"] = Add(Unconsumed), ["bAddItemSPHealRate"] = Add(Unconsumed), // itemhealrate2 / itemsphealrate2
        ["bMatk"] = Add(F17), ["bMatk2"] = Add(F17),             // SP_EMATK / SP_EMATK_HIDDEN
        ["bVariableCast"] = Add(F18),                            // add_varcast
        ["bFixedCastrate"] = Of(EquipmentBonusCombine.Min, F18), // RENEWAL_CAST fixcastrate = min(fixcastrate, val)
        ["bFixedCast"] = Add(F18),                               // add_fixcast
        ["bCastrate"] = Add(F18, scale: -1), ["bVariableCastrate"] = Add(F18, scale: -1), // RENEWAL_CAST varcastrate -= val
        ["bAddMaxWeight"] = Add(Unconsumed),
        ["bAbsorbDmgMaxHP"] = Of(EquipmentBonusCombine.Max, F17, arrow: Shr), ["bAbsorbDmgMaxHP2"] = Of(EquipmentBonusCombine.Max, F17, arrow: Shr),
        ["bCriticalLong"] = Add(F17, scale: 10, arrow: Sep),     // critical_rangeatk / arrow_cri += val*10
        ["bWeaponAtkRate"] = Add(F17), ["bWeaponMatkRate"] = Add(F17),
        ["bNoMadoFuel"] = Of(EquipmentBonusCombine.Flag, F18), ["bNoWalkDelay"] = Of(EquipmentBonusCombine.Flag, F17),
    };
}

// The combined single-value pc_bonus parameters of every equipped script, by (constant name, hand). A parameter no
// equipped script touched is absent (pinned value 0). Values are typed by EquipmentBonusParameters.ByName[name].
public sealed record EquipmentFlatBonuses(IReadOnlyDictionary<(string Name, EquipmentBonusHand Hand), int> Values)
{
    public static readonly EquipmentFlatBonuses Empty = new(new Dictionary<(string, EquipmentBonusHand), int>());

    public int Get(string name, EquipmentBonusHand hand = EquipmentBonusHand.Right) => Values.GetValueOrDefault((name, hand));
}
