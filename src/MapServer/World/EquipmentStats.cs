using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.World;

// Equipment contribution to the status calculation for the supported scope: hard DEF summed over every equipped armor row,
// the right-hand weapon, flat primary-stat bonuses from every equipped item's EquipScript (EquipmentBonusResolver), and
// weapon/armor refine ATK/MATK/DEF bonuses (GeneratedRefineBonus - see IStatRules.TryResolveEquipment's own doc comment
// for the exact pinned formula and what remains deferred: the enchant-grade multiplier, since enchantgrade_db is not yet
// generated). Plain data - how it is resolved from the inventory is a regional rule (IStatRules.TryResolveEquipment),
// which rejects an equipped item it cannot account for instead of assuming "no effect".
public readonly record struct EquipmentStats(
    int HardDefense, WeaponItemDefinition? Weapon, EquipmentParamBonus ParamBonus = default,
    int WeaponRefineAtk = 0, int WeaponRefineMatk = 0, int ArmorRefineDefense = 0)
{
    public int WeaponAttack => (Weapon?.Attack ?? 0) + WeaponRefineAtk;
}
