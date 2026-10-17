using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

// Equipment contribution to the status calculation for the supported scope: hard DEF summed over every equipped
// armor row (status_calc_pc_sub adds each equipped item's def to base_status->def, status.cpp equipment pass) and the
// right-hand weapon. Refine, cards, random options, item scripts and combos are NOT modeled: an equipped item the
// registry does not know, or of a type this pass cannot account for, makes the whole contribution unresolved instead
// of silently counting as "no effect" (callers mirror the existing unresolved-weapon abort).
public readonly record struct EquipmentStats(int HardDefense, WeaponItemDefinition? Weapon)
{
    public int WeaponAttack => Weapon?.Attack ?? 0;

    public static bool TryFromInventory(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items, out EquipmentStats stats)
    {
        var hardDefense = 0;
        WeaponItemDefinition? weapon = null;
        foreach (var row in inventory.Items)
        {
            if (row.Equip == 0) continue; // One inventory row counts once however many slot bits it carries.
            if (!items.TryGetValue(row.ItemId, out var definition)) { stats = default; return false; }
            switch (definition)
            {
                case ArmorItemDefinition armor:
                    hardDefense += armor.Defense;
                    break;
                case WeaponItemDefinition w when (row.Equip & 0x000002) != 0:
                    weapon = w;
                    break;
                default:
                    stats = default;
                    return false;
            }
        }
        stats = new EquipmentStats(hardDefense, weapon);
        return true;
    }
}
