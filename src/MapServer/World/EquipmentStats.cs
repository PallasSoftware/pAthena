using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.World;

// Equipment contribution to the status calculation for the supported scope: hard DEF summed over every equipped armor row,
// the right-hand weapon, flat primary-stat bonuses from every equipped item's EquipScript (EquipmentBonusResolver), and
// weapon/armor refine ATK/MATK/DEF bonuses (GeneratedRefineBonus - see IStatRules.TryResolveEquipment's own doc comment
// for the exact pinned formula and what remains deferred: the enchant-grade multiplier, since enchantgrade_db is not yet
// generated). Plain data - how it is resolved from the inventory is a regional rule (IStatRules.TryResolveEquipment),
// which rejects an equipped item it cannot account for instead of assuming "no effect".
//
// OWNERSHIP BOUNDARY (Feature 16 vs Feature 17): WeaponRefineAtk/WeaponRefineMatk are typed, correctly-
// computed Feature 16 data, but the LIVE player-vs-monster damage formula (IroCombatRules/
// KroCombatRules.CalculatePlayerBasicAttack, via EquippedWeaponResolver) reads WeaponItemDefinition.Attack
// directly from a separately-resolved weapon with NO refine/EquipmentStats context at all - it does not
// consume WeaponAttack below yet. Feature 16 does not claim refine ATK is live in combat; wiring
// battle_calc_base_weapon_attack's watk term to read refine-adjusted ATK (battle.cpp:2440-2490) is Feature
// 17's own integration work. See EquipmentStatsRefineBoundaryTests for the regression proving this gap is
// explicit, not silently "working".
public readonly record struct EquipmentStats(
    int HardDefense, WeaponItemDefinition? Weapon, EquipmentParamBonus ParamBonus = default,
    int WeaponRefineAtk = 0, int WeaponRefineMatk = 0, int ArmorRefineDefense = 0)
{
    // The CORRECT total static+refine ATK per pinned wa->atk + wa->atk2 (status.cpp:3952,3957) - ready for
    // Feature 17 to consume once CalculatePlayerBasicAttack is updated to take EquipmentStats/this value
    // instead of a bare WeaponItemDefinition. Not yet read by any live combat path (see this type's own
    // doc comment above).
    public int WeaponAttack => (Weapon?.Attack ?? 0) + WeaponRefineAtk;
}
