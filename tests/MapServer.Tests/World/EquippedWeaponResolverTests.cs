using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class EquippedWeaponResolverTests
{
    private static readonly WeaponItemDefinition Knife = new(
        1201, "Knife", "Knife", Stackable: false, ClientViewId: 1201, Attack: 17, WeaponLevel: 1, WeaponType.Dagger, Range: 1, EquipLocation: 0x000002,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static readonly EtcItemDefinition Wood = new(
        6008, "Wood", "Wood", Stackable: true, ClientViewId: 6008,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_etc.yml", 1));

    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items =
        new Dictionary<int, ItemDefinition> { [Knife.Id] = Knife, [Wood.Id] = Wood };

    [Fact]
    public void Resolve_NoRightHandItem_ReturnsUnarmed()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: null, RightHandRefine: 0), Items);

        Assert.Equal(EquippedWeaponResolution.Unarmed, result.Resolution);
        Assert.Null(result.Weapon);
    }

    [Fact]
    public void Resolve_KnifeEquipped_ReturnsWeaponWithDaggerType()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 1201, RightHandRefine: 3), Items);

        Assert.Equal(EquippedWeaponResolution.Weapon, result.Resolution);
        Assert.Equal(Knife, result.Weapon);
        Assert.Equal(WeaponType.Dagger, result.Weapon!.WeaponType);
    }

    [Fact]
    public void Resolve_UnregisteredItemId_ReturnsUnknownItem_NotUnarmed()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 999999, RightHandRefine: 0), Items);

        Assert.Equal(EquippedWeaponResolution.UnknownItem, result.Resolution);
        Assert.Null(result.Weapon);
    }

    [Fact]
    public void Resolve_NonWeaponItemInRightHand_ReturnsNonWeaponInWeaponSlot_NotUnarmed()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 6008, RightHandRefine: 0), Items);

        Assert.Equal(EquippedWeaponResolution.NonWeaponInWeaponSlot, result.Resolution);
        Assert.Null(result.Weapon);
    }

    // Defect 5 boundary (deliberately NOT a bug fix): CharacterEquipmentSnapshot.RightHandRefine is
    // real, correctly-persisted data, but EquippedWeaponResult only carries the WeaponItemDefinition
    // itself - the refine value is discarded here, matching EquipmentStats's own doc comment that
    // the live player-vs-monster damage path (IroCombatRules/KroCombatRules.CalculatePlayerBasicAttack,
    // which consumes EquippedWeaponResolver's own result) does not yet see refine ATK at all. Two
    // equipment snapshots differing ONLY in RightHandRefine must resolve to the IDENTICAL weapon
    // result - if this test ever starts failing because RightHandRefine began influencing the
    // resolved Weapon, Feature 17's own combat-formula integration is the place that boundary should
    // move, with its own typed contract and tests, not a silent side effect of this resolver.
    [Fact]
    public void Resolve_DoesNotReflectRightHandRefine_ResultIsIdenticalRegardlessOfRefineValue()
    {
        var unrefined = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 1201, RightHandRefine: 0), Items);
        var refined = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 1201, RightHandRefine: 10), Items);

        Assert.Equal(unrefined, refined);
        Assert.Equal(Knife.Attack, refined.Weapon!.Attack); // the item's own static Attack, never refine-adjusted here.
    }
}
