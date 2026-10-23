using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World.Items;

// Pinned pc_insert_card (pc.cpp:5596-5652), restricted to this project's modeled checks: compound
// one card into the first free slot of an unequipped, identified weapon/armor row. The success/
// failure client packet and the actual compound-UI trigger are Protocol-pending-capture (see
// ai/iro-2026-wire.md); this resolver exists so Feature 18/20's eventual skill/NPC-script call site
// has a validated, idempotent target slot to hand CharacterItemCardService, without Feature 16
// inventing a packet to get there.
public enum CardInsertFailureReason
{
    UnknownEquipRow, UnknownCardRow, NotWeaponOrArmor, NotACard, EquipTargetNotIdentified,
    ReservedCardSlot, IncompatibleLocation, ShieldCardOnLeftHandWeapon, AccessorySpecificMismatch,
    EquipTargetCurrentlyEquipped, NoFreeSlot, PersistenceFailed,
}

public static class ItemCardInsertResolver
{
    // Pinned itemdb_isspecial (itemdb.hpp:38): card[0] holding one of these sentinels means the slot
    // array is reserved for forge/crafter/pet-rename attribution, never a real card.
    private const uint Card0Forge = 0x00FF;
    private const uint Card0Create = 0x00FE;
    private const uint Card0Pet = 0x0100;

    public static bool TryResolve(
        ItemDefinition? equipTarget, CharacterInventoryItem equipRow,
        ItemDefinition? card, CharacterInventoryItem cardRow,
        out int freeSlotIndex, out CardInsertFailureReason failure)
    {
        freeSlotIndex = -1;
        if (equipTarget is null) { failure = CardInsertFailureReason.UnknownEquipRow; return false; }
        if (card is null) { failure = CardInsertFailureReason.UnknownCardRow; return false; }
        if (equipTarget is not (WeaponItemDefinition or ArmorItemDefinition)) { failure = CardInsertFailureReason.NotWeaponOrArmor; return false; }
        if (card is not CardItemDefinition cardDefinition) { failure = CardInsertFailureReason.NotACard; return false; }
        if (!equipRow.Identified) { failure = CardInsertFailureReason.EquipTargetNotIdentified; return false; }

        var equipLocation = ((IEquippableItemDefinition)equipTarget).EquipLocation;
        var cards = new[] { equipRow.Details.Card0, equipRow.Details.Card1, equipRow.Details.Card2, equipRow.Details.Card3 };
        if (cards[0] is Card0Forge or Card0Create or Card0Pet) { failure = CardInsertFailureReason.ReservedCardSlot; return false; }
        if ((equipLocation & cardDefinition.EquipLocations) == 0) { failure = CardInsertFailureReason.IncompatibleLocation; return false; }
        if (equipTarget is WeaponItemDefinition && cardDefinition.EquipLocations == EquipSlots.Bitmask[EquipSlot.HandLeft])
        { failure = CardInsertFailureReason.ShieldCardOnLeftHandWeapon; return false; }
        if (equipTarget is ArmorItemDefinition && (cardDefinition.EquipLocations & EquipSlots.Acc) != 0
            && (cardDefinition.EquipLocations & EquipSlots.Acc) != EquipSlots.Acc
            && (equipLocation & EquipSlots.Acc) != (cardDefinition.EquipLocations & EquipSlots.Acc))
        { failure = CardInsertFailureReason.AccessorySpecificMismatch; return false; }
        if (equipRow.Equip != 0) { failure = CardInsertFailureReason.EquipTargetCurrentlyEquipped; return false; }

        // ARR_FIND(0, item_eq->slots, i, card[i] == 0): only searches within the item's OWN slot
        // count, never the full 4-length card array (an item with Slots=1 never gets a second card
        // even though card[1..3] are structurally present and zero).
        var totalSlots = (int)(equipTarget.Meta?.Slots ?? 0);
        var slot = -1;
        for (var i = 0; i < totalSlots && i < cards.Length; i++) { if (cards[i] == 0) { slot = i; break; } }
        if (slot < 0) { failure = CardInsertFailureReason.NoFreeSlot; return false; }

        freeSlotIndex = slot;
        failure = default;
        return true;
    }
}
