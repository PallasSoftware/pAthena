using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Regional character stat rules: six stats with job bonuses, derived stats (incl. MATK), max HP/SP and the resource
// transition on a stat change, equipment-derived DEF/weapon, and stat-point allocation cost/validation.
public interface IStatRules
{
    PrimaryStats BasePrimaryStats(CharacterGameplayState state);

    // Pinned status_calc_pc (status.cpp:4290-4301): base_status->str/agi/vit/int_/dex/luk already
    // include param_bonus/param_equip (equipment) BEFORE status_calc_bl_main ever applies a status
    // effect on top - equipment and status are NOT two independent additive layers over the same
    // raw base, equipment is baked into the base status_calc_bl_main's own delta math operates on.
    // Callers computing an EFFECTIVE (status-included) stat for anything equipment can also affect
    // MUST apply this first, then apply status deltas on the result - never apply status deltas to
    // the raw BasePrimaryStats and equipment separately to another copy of the same raw base, which
    // silently drops whichever contribution is computed second from ever reaching a shared result.
    PrimaryStats ApplyEquipmentBonus(PrimaryStats baseStats, EquipmentParamBonus bonus);

    DerivedStats CalculateDerivedStats(CharacterStatusSnapshot snapshot, EquipmentStats equipment = default);
    uint MaxHp(CharacterGameplayState state, int effectiveVitality);
    uint MaxSp(CharacterGameplayState state, int effectiveIntelligence);

    // Pinned status_calc_maxhp_pc / status_calc_maxsp_pc for the wearer in `context` (equipment, learned skills, sex).
    (uint MaxHp, uint MaxSp) MaxResources(CharacterGameplayState state, MaxResourceContext context);

    // New maxima (MaxResources); current values are only clamped down, never healed.
    CharacterGameplayState WithRecalculatedResources(CharacterGameplayState state, MaxResourceContext context = default);

    // False when an equipped item cannot be accounted for (unknown or unsupported type) instead of assuming "no effect".
    // character is the wearer equipment scripts may read (BaseLevel, readparam, getskilllv); without it those scripts are
    // unsupported rather than evaluated against a made-up character.
    bool TryResolveEquipment(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items, out EquipmentStats equipment, EquipmentCharacter? character = null);

    uint StatIncreaseCost(ushort currentValue, int increaseAmount);
    StatIncreaseValidationResult ValidateStatIncrease(CharacterGameplayState gameplay, CharacterBaseStat stat, int increaseAmount);
}

// What besides the gameplay state feeds max HP/SP: the resolved equipment (VIT/INT, param_equip, bMaxHP/SP[rate]), the learned
// skills with HP/SP bonuses and the sex (Bard/Dancer lesson). default = no equipment, no skills.
public readonly record struct MaxResourceContext(EquipmentStats Equipment, CharacterSkillSnapshot? Skills = null, byte Sex = 1);
