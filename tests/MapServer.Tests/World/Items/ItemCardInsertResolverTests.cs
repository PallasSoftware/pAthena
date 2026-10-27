using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.Tests.World.Items;

public sealed class ItemCardInsertResolverTests
{
    // Real pinned items (db/re/item_db_etc.yml / item_db_equip.yml), matching this project's own
    // established convention of testing against real generated data rather than synthetic fixtures
    // whenever a real item exercises the exact case.
    private static readonly ItemDefinition PoringCard = ItemCatalog.Current.ByAegisName["Poring_Card"]; // Locations: Armor.
    private static readonly ItemDefinition CottonShirt = ItemCatalog.Current.ByAegisName["Cotton_Shirt_"]; // Armor, Slots: 1 (id 2302, the slotted variant).
    private static readonly ItemDefinition Knife = ItemCatalog.Current.ByAegisName["Knife"]; // Weapon, EQP_HAND_R.
    private static readonly ItemDefinition Wood = ItemCatalog.Current.ByAegisName["Wood"]; // Etc, not a card.

    private static CharacterInventoryItem Row(uint durableId, ItemDefinition item, bool identified = true, uint equip = 0, uint card0 = 0) =>
        new(durableId, SlotIndex: durableId, item.Id, Amount: 1, equip, identified, Refine: 0, Favorite: 0, Bound: 0,
            Detail: ItemInstanceDetail.Empty with { Card0 = card0 });

    [Fact]
    public void TryResolve_CompatibleArmorAndCard_FirstFreeSlot_Succeeds()
    {
        Assert.Equal(1u, CottonShirt.Meta!.Slots); // Id 2302's pinned Slots: 1 - this test depends on it.
        var armor = Row(1, CottonShirt);
        var card = Row(2, PoringCard);

        var resolved = ItemCardInsertResolver.TryResolve(CottonShirt, armor, PoringCard, card, out var slot, out _);
        Assert.True(resolved);
        Assert.Equal(0, slot);
    }

    [Fact]
    public void TryResolve_UnknownEquipTarget_ReturnsUnknownEquipRow()
    {
        var card = Row(2, PoringCard);
        var resolved = ItemCardInsertResolver.TryResolve(null, Row(1, CottonShirt), PoringCard, card, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.UnknownEquipRow, failure);
    }

    [Fact]
    public void TryResolve_UnknownCardItem_ReturnsUnknownCardRow()
    {
        var armor = Row(1, CottonShirt);
        var resolved = ItemCardInsertResolver.TryResolve(CottonShirt, armor, null, Row(2, PoringCard), out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.UnknownCardRow, failure);
    }

    [Fact]
    public void TryResolve_EquipTargetIsEtcItem_ReturnsNotWeaponOrArmor()
    {
        var target = Row(1, Wood);
        var card = Row(2, PoringCard);
        var resolved = ItemCardInsertResolver.TryResolve(Wood, target, PoringCard, card, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.NotWeaponOrArmor, failure);
    }

    [Fact]
    public void TryResolve_CardArgumentIsNotACardItem_ReturnsNotACard()
    {
        var armor = Row(1, CottonShirt);
        var notACard = Row(2, Wood);
        var resolved = ItemCardInsertResolver.TryResolve(CottonShirt, armor, Wood, notACard, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.NotACard, failure);
    }

    [Fact]
    public void TryResolve_UnidentifiedEquipTarget_ReturnsEquipTargetNotIdentified()
    {
        var armor = Row(1, CottonShirt, identified: false);
        var card = Row(2, PoringCard);
        var resolved = ItemCardInsertResolver.TryResolve(CottonShirt, armor, PoringCard, card, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.EquipTargetNotIdentified, failure);
    }

    [Fact]
    public void TryResolve_EquippedTarget_ReturnsEquipTargetCurrentlyEquipped()
    {
        var armor = Row(1, CottonShirt, equip: 0x000010);
        var card = Row(2, PoringCard);
        var resolved = ItemCardInsertResolver.TryResolve(CottonShirt, armor, PoringCard, card, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.EquipTargetCurrentlyEquipped, failure);
    }

    [Fact]
    public void TryResolve_ArmorCardOnWeapon_ReturnsIncompatibleLocation()
    {
        var knife = Row(1, Knife);
        var card = Row(2, PoringCard); // Armor-only card.
        var resolved = ItemCardInsertResolver.TryResolve(Knife, knife, PoringCard, card, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.IncompatibleLocation, failure);
    }

    [Fact]
    public void TryResolve_ReservedForgeCardSlot_ReturnsReservedCardSlot()
    {
        const uint card0Forge = 0x00FF;
        var armor = Row(1, CottonShirt, card0: card0Forge);
        var card = Row(2, PoringCard);
        var resolved = ItemCardInsertResolver.TryResolve(CottonShirt, armor, PoringCard, card, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.ReservedCardSlot, failure);
    }

    [Fact]
    public void TryResolve_NoFreeSlot_ReturnsNoFreeSlot()
    {
        // CottonShirt_ (id 2302) has pinned Slots: 1 - already filled, so no slot is free.
        var armor = Row(1, CottonShirt, card0: (uint)PoringCard.Id);
        var card = Row(2, PoringCard);

        var resolved = ItemCardInsertResolver.TryResolve(CottonShirt, armor, PoringCard, card, out _, out var failure);
        Assert.False(resolved);
        Assert.Equal(CardInsertFailureReason.NoFreeSlot, failure);
    }
}
