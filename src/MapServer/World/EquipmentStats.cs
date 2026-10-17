namespace Athena.Net.MapServer.World;

// Equipment contribution to the status calculation for the supported scope: hard DEF summed over every equipped armor row
// and the right-hand weapon. Plain data - how it is resolved from the inventory is a regional rule
// (IStatRules.TryResolveEquipment), which rejects an equipped item it cannot account for instead of assuming "no effect".
public readonly record struct EquipmentStats(int HardDefense, WeaponItemDefinition? Weapon)
{
    public int WeaponAttack => Weapon?.Attack ?? 0;
}
