using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.Tests.Gameplay.Rules;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.World;

// Pinned itemdb_isNoEquip over the generated static map flags (npc/re/mapflag/*.txt and every other active mapflag line).
public sealed class MapFlagSetTests
{
    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items = ItemCatalog.Current.ById;
    private static uint NoEquip(int itemId) => Items[itemId].Meta!.NoEquip;

    [Theory]
    // 2483 Siege_Greave: item_noequip.txt "2483,1" (normal maps) and "2483,16" (WoE:TE).
    [InlineData(2483, "prt_fild08", true)]    // a plain field: normal
    [InlineData(2483, "pvp_y_1-1", false)]    // pvp: not normal, no PVP bit
    [InlineData(2483, "guild_vs1", false)]    // gvg: not normal, no GvG bit
    [InlineData(2483, "te_aldecas1", true)]   // gvg_te_castle: the WoE:TE bit
    // 14529 Greed_Scroll: "14529,2048" = zone 7 ((1 << 8) << 3), which npc/re/mapflag/restricted.txt sets on towns.
    [InlineData(14529, "prontera", true)]
    [InlineData(14529, "prt_fild08", false)]
    public void IsNoEquip_FollowsPinnedMapTypesAndZones(int itemId, string map, bool restricted) =>
        Assert.Equal(restricted, MapFlagSet.For(map).IsNoEquip(NoEquip(itemId)));

    [Fact]
    public void GeneratedFlags_CarryPinnedTypesAndZone()
    {
        Assert.Equal(new MapFlagSet(MapFlag.Restricted, 0x800), MapFlagSet.For("prontera"));
        Assert.Equal(MapFlag.Pvp | MapFlag.Restricted, MapFlagSet.For("sec_pri").Flags); // npc/mapflag/jail.txt
        Assert.Equal(default, MapFlagSet.For("not_a_map"));
    }

    private static CharacterInventoryItem Row(uint id, int itemId, uint equip, uint card0 = 0) =>
        new(id, id - 1, itemId, 1, equip, true, 0, 0, 0, ItemInstanceDetail.Empty with { Card0 = card0 });

    [Fact]
    public void RestrictedEquipment_StaysWorn_ButItsScriptGivesNoEffect_WhileAnUnrestrictedCardInItStillApplies()
    {
        // Pinned status_calc_pc with allow_equip_restricted_item: yes: the item's own script is skipped (status.cpp:4024), base DEF
        // is not, and a card is judged by its OWN restriction (status.cpp:4132) - 4097 Matyr_Card `bonus bMaxHPrate,10;` is not.
        var rules = TestGameplayRules.Services();
        var greave = Row(1, 2483, ((IEquippableItemDefinition)Items[2483]).EquipLocation, card0: 4097); // + a shoes card
        bool Restricted(ItemDefinition item) => MapFlagSet.For("prt_fild08").IsNoEquip(item.Meta?.NoEquip ?? 0);

        Assert.True(rules.Stats.TryResolveEquipment(new([greave]), Items, out var anywhere));
        Assert.True(rules.Stats.TryResolveEquipment(new([greave]), Items, out var field, noEquip: Restricted));

        Assert.Equal(1, anywhere.FlatBonusesOrEmpty.Get("bMdef"));
        Assert.Equal(0, field.FlatBonusesOrEmpty.Get("bMdef"));
        Assert.Equal(anywhere.HardDefense, field.HardDefense);
        Assert.Equal((10, 10), (anywhere.ParamBonus.MaxHpRate, field.ParamBonus.MaxHpRate));
    }

    [Fact]
    public void ARestrictedCard_IsSkipped_WhileItsCarrierStillApplies()
    {
        var rules = TestGameplayRules.Services();
        var shirt = Row(1, 2301, 0x10, card0: 4226); // Sting_Card `bonus bDef,2;` in Cotton_Shirt
        Assert.True(rules.Stats.TryResolveEquipment(new([shirt]), Items, out var skipped, noEquip: item => item is CardItemDefinition));
        Assert.True(rules.Stats.TryResolveEquipment(new([shirt]), Items, out var applied, noEquip: _ => false));

        Assert.Equal(0, skipped.FlatBonusesOrEmpty.Get("bDef"));
        Assert.Equal(2, applied.FlatBonusesOrEmpty.Get("bDef"));
    }
}
