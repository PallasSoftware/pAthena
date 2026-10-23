using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace MapServer.Tests.World.Items;

public sealed class ItemCatalogTests
{
    private static ItemCatalog Catalog => ItemCatalog.Current;

    [Fact]
    public void Catalog_HoldsEveryPinnedItemWithMetadata()
    {
        // 29,356 pinned rows + the loader's ITEMID_DUMMY (499).
        Assert.Equal(29_357, Catalog.ById.Count);
        Assert.Equal("e985006171d2eb320ee512a653f4c83aea3d81b6", Catalog.RathenaCommit);
        Assert.All(Catalog.ById.Values, definition =>
        {
            Assert.NotNull(definition.Meta);
            Assert.Equal((uint)definition.Id, definition.Meta!.Data.Id);
        });
    }

    [Fact]
    public void Catalog_KeepsTheModeledStarterItemsIdenticalToTheirPreviousHandPickedForm()
    {
        var knife = GeneratedItems.Knife;
        Assert.Equal((1201, "Knife", false, 1201, 17, 1, WeaponType.Dagger, 1, 0x2u, 0), (knife.Id, knife.AegisName, knife.Stackable, knife.ClientViewId, knife.Attack, knife.WeaponLevel, knife.WeaponType, knife.Range, knife.EquipLocation, knife.MagicAttack));
        var shirt = GeneratedItems.CottonShirt;
        Assert.Equal((2301, 10, 0x10u, false), (shirt.Id, shirt.Defense, shirt.EquipLocation, shirt.Stackable));
        Assert.Equal((6008, true), (GeneratedItems.Wood.Id, GeneratedItems.Wood.Stackable));
    }

    [Fact]
    public void Catalog_ContainersKeepTheirConstantGetItemGrants()
    {
        var box = GeneratedItems.FirstaidBox10;
        Assert.Equal(
            [(11518, 15u), (11614, 40u), (12325, 15u), (22542, 1u), (23486, 1u)],
            box.Grants.Select(grant => (grant.ItemId, grant.Amount)).ToArray());
        Assert.NotEmpty(GeneratedItems.FirstAidBox.Grants);
    }

    [Fact]
    public void Catalog_ModelsEveryPinnedItemType()
    {
        Assert.IsType<DelayConsumeItemDefinition>(GeneratedItems.Magnifier);
        Assert.IsType<HealingItemDefinition>(GeneratedItems.BluePotion);
        Assert.Contains(Catalog.ById.Values, definition => definition is CardItemDefinition { AegisName: "Poring_Card" });
        Assert.Contains(Catalog.ById.Values, definition => definition is AmmoItemDefinition { AegisName: "Arrow" });
        Assert.Contains(Catalog.ById.Values, definition => definition is ShadowGearItemDefinition);
        Assert.Contains(Catalog.ById.Values, definition => definition is PetEggItemDefinition);
        Assert.Contains(Catalog.ById.Values, definition => definition is PetArmorItemDefinition);
        Assert.Contains(Catalog.ById.Values, definition => definition is CashItemDefinition);
        Assert.Equal(5_593, Catalog.ById.Values.Count(definition => definition is CardItemDefinition));
        Assert.Equal(2_806, Catalog.ById.Values.Count(definition => definition is WeaponItemDefinition));
        Assert.Equal(8_954, Catalog.ById.Values.Count(definition => definition is ArmorItemDefinition));
    }

    // Pinned item_db_usable.yml Id 607 (AegisName Reagent_Boost_Wind or similar percentheal-script
    // item) carries Delay: { Duration: 5000, Status: Reuse_Limit_F } - a real pinned Delay group,
    // proving ItemMetadata.HasDelayGroup/DelayGroupKey/DelayDurationMilliseconds reach the catalog's
    // actual loaded data, not just a synthetic PackItem. Id 1201 (Knife, no Delay block) proves the
    // negative case through the same real catalog.
    [Fact]
    public void Catalog_ExposesDelayGroupThroughMeta()
    {
        var withDelay = Catalog.ById[607];
        Assert.True(withDelay.Meta!.HasDelayGroup);
        Assert.Equal(5000u, withDelay.Meta.DelayDurationMilliseconds);

        var withoutDelay = Catalog.ById[1201];
        Assert.False(withoutDelay.Meta!.HasDelayGroup);
    }

    [Fact]
    public void Catalog_ExposesLoaderFieldsThroughMeta()
    {
        var sword = Catalog.ById[1101];
        Assert.Equal(3u, sword.Meta!.Slots);
        Assert.Equal(100, sword.Meta.Buy);
        Assert.Equal(500u, sword.Meta.Weight);
        Assert.True(sword.Meta.Refineable);
        Assert.Equal(2u, sword.Meta.Data.EquipLevelMin);
        // The Taurus Sword's script mixes conditionals, a local variable and several bonus commands.
        var script = Catalog.ById[1100].Meta!.Script;
        Assert.NotNull(script);
        Assert.Equal(PackScriptSupport.Lowered, script!.Support);
        Assert.True(script.Uses.HasFlag(PackScriptUses.Bonus));
    }

    // Pinned db/re/item_noequip.txt row "2483,1" (Siege_Greave) sets bit 1 ("restricted in normal maps"), the only
    // nouse_mapflag bit this project currently enforces (see ItemMetadata.RestrictedOnNormalMaps). Knife (1201) has
    // no item_noequip.txt row at all, proving the negative case through the real catalog, not a synthetic PackItem.
    [Fact]
    public void Catalog_ExposesNoEquipBitOneThroughMeta()
    {
        var restricted = Catalog.ById[2483];
        Assert.True(restricted.Meta!.RestrictedOnNormalMaps);

        var unrestricted = Catalog.ById[1201];
        Assert.False(unrestricted.Meta!.RestrictedOnNormalMaps);
    }

    // The pack must carry the COMPLETE source item_noequip.txt bitmask, not a pre-filtered "bit 1 only" value - 2483
    // (Siege_Greave) has separate rows "2483,1" (normal maps) AND "2483,16" (WOE:TE), so both bits must survive the
    // loader/pack pipeline into Data.NoEquip verbatim even though only bit 1 is currently runtime-enforced (see
    // ItemMetadata.RestrictedOnNormalMaps's documented scope gap). This guards against silently truncating source data.
    [Fact]
    public void Catalog_PreservesTheFullNoEquipBitmaskNotJustTheEnforcedBit()
    {
        var item = Catalog.ById[2483];
        Assert.Equal(1u | 16u, item.Meta!.Data.NoEquip & (1u | 16u));
    }

    [Fact]
    public void Catalog_ClientViewIdFollowsAliasName()
    {
        // client_nameid(): an item whose row declares an AliasName is shown to the client under the alias' id.
        var aliased = Catalog.ById.Values.First(definition => definition.Meta!.Data.ViewId != 0);
        Assert.Equal((int)aliased.Meta!.Data.ViewId, aliased.ClientViewId);
        Assert.Equal(1201, GeneratedItems.Knife.ClientViewId);
    }
}
