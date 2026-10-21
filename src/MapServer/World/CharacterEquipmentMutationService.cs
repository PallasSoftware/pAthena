using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned enum equip_index (pc.hpp:73-96) - the EQI_* slot array pc_equipitem/pc_unequipitem
// use to detect slot conflicts and swap out an already-equipped item. Only the slots this
// domain model can currently produce items for are modeled (EQI_HEAD_LOW/MID/TOP, EQI_ARMOR,
// EQI_HAND_L/R, EQI_SHOES, EQI_GARMENT, EQI_ACC_L/R, EQI_AMMO) - costume/shadow slots are
// omitted, matching ItemDefinition's own shallow-extension convention; extend only when a
// traced item needs them.
public enum EquipSlot
{
    AccLeft,
    AccRight,
    Shoes,
    Garment,
    HeadLow,
    HeadMid,
    HeadTop,
    Armor,
    HandLeft,
    HandRight,
    Ammo,
}

public static class EquipSlots
{
    // Pinned equip_bitmask[EQI_MAX] (pc.cpp:693-709), restricted to the slots EquipSlot models.
    public static readonly IReadOnlyDictionary<EquipSlot, uint> Bitmask = new Dictionary<EquipSlot, uint>
    {
        [EquipSlot.AccLeft] = 0x000080,
        [EquipSlot.AccRight] = 0x000008,
        [EquipSlot.Shoes] = 0x000040,
        [EquipSlot.Garment] = 0x000004,
        [EquipSlot.HeadLow] = 0x000001,
        [EquipSlot.HeadMid] = 0x000200,
        [EquipSlot.HeadTop] = 0x000100,
        [EquipSlot.Armor] = 0x000010,
        [EquipSlot.HandLeft] = 0x000020,
        [EquipSlot.HandRight] = 0x000002,
        [EquipSlot.Ammo] = 0x008000,
    };

    // Pinned EQP_ARMS = EQP_HAND_R|EQP_HAND_L (pc.hpp:1132).
    public const uint Arms = 0x000002 | 0x000020;

    // Pinned EQP_ACC = EQP_ACC_L|EQP_ACC_R (pc.hpp:1134).
    public const uint Acc = 0x000080 | 0x000008;
}

public enum EquipMutationResult
{
    // Pinned enum clif_equipitemack_flag, PACKETVER_RE_NUM >= 20121107 branch (clif.hpp:522-533): OK=0, FAILLEVEL=1, FAIL=2.
    Success,
    FailLevel,
    Fail,
}

public readonly record struct EquipOutcome(EquipMutationResult Result, uint WearLocation);
public readonly record struct UnequipOutcome(bool Success, uint WearLocation);

// Runtime equip/unequip mutation - the pinned PACKETVER 20220406 equivalent of
// pc_equipitem/pc_unequipitem (pc.cpp:12015-12240, 12398-12500), scoped to the structural
// slot-conflict/EQP_ARMS logic those functions implement plus pc_isequip's (pc.cpp:1850-1908)
// level/sex/broken/restricted-map eligibility gate. Explicitly NOT modeled (Feature 16 stage S4,
// documented as a tracked gap, not silently skipped): pc_job_can_use_item/pc_isItemClass job-class
// eligibility (ClassBase0/1/2/ClassUpper ARE already in the generated item data - PackItem/
// ItemMetadata - but this project has no JobClass -> pinned MAPID_*/JOBL_2_1/JOBL_2_2/JOBL_UPPER/
// JOBL_BABY/JOBL_THIRD/JOBL_FOURTH tier-bitmask mapping yet; that is substantial, separate
// infrastructure, not a quick addition to this file), status-effect equip-blocking (SC__STRIPACCESSORY
// etc. - Feature 17/status-effect territory), and ammo/weapon-type compatibility (ammo_check_weapon).
//
// CharServer remains the durable owner of CharInventory.Equip. An equip is ONE atomic inventory operation that unequips every
// conflicting item AND equips the target, so a failure can never leave the old item unequipped while the new one is not worn.
// The runtime snapshot is only replaced by the confirmed result of that operation (CharacterInventoryService), and success is
// only reported after it.
public sealed class CharacterEquipmentMutationService(CharacterInventoryService inventoryService)
{
    // Pinned pc_equipitem (pc.cpp:12015-12172), restricted to this service's modeled checks:
    //   1. already-equipped items.equip != 0 -> ITEM_EQUIP_ACK_FAIL (pc.cpp:12063-12070)
    //   2. requested position must intersect the item's EquipLocation -> ITEM_EQUIP_ACK_FAIL
    //   3. pc_isequip's own eligibility gate (pc.cpp:1850-1908): EquipLevelMin/Max -> FAILLEVEL,
    //      Sex mismatch -> FAIL, broken (Attribute != 0) -> FAIL, restricted-map -> FAIL (see
    //      ItemMetadata.RestrictedOnNormalMaps's own doc comment for this project's current
    //      "every reachable map is normal" scope).
    //   4. EQP_ARMS dual-wield: if the item's own EquipLocation is exactly EQP_HAND_R and the
    //      caller requested EQP_ARMS (both hands), pick whichever hand is free, preferring
    //      HAND_L when both are free (pc.cpp:12108-12115, RENEWAL branch)
    //   5. slot conflict: any currently-equipped item occupying one of the resolved position's
    //      EQI slots is unequipped first (pc.cpp:12147-12157)
    // Returns a null outcome if the requested slotIndex is not a real row in the snapshot (the caller turns that into no ack,
    // matching pc_equipitem's own `if (!(id = sd->inventory_data[n])) return false;` early-out).
    public async Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> EquipAsync(
        uint slotIndex,
        uint requestedPosition,
        IReadOnlyDictionary<int, ItemDefinition> items,
        uint characterBaseLevel,
        byte characterSex,
        CancellationToken cancellationToken)
    {
        EquipOutcome? decided = null;
        uint wearLocation = 0;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Equip, inventory =>
        {
            var plan = PlanEquip(inventory, slotIndex, requestedPosition, items, characterBaseLevel, characterSex, out var outcome, out wearLocation);
            decided = outcome;
            return plan;
        }, cancellationToken);

        if (decided is null) return (null, null);
        if (decided.Value.Result != EquipMutationResult.Success) return (decided, null);
        return change.Succeeded
            ? (new EquipOutcome(EquipMutationResult.Success, wearLocation), change.Snapshot)
            : (new EquipOutcome(EquipMutationResult.Fail, 0), null);
    }

    private static IReadOnlyList<InventoryMutation>? PlanEquip(
        CharacterInventorySnapshot inventory, uint slotIndex, uint requestedPosition,
        IReadOnlyDictionary<int, ItemDefinition> items, uint characterBaseLevel, byte characterSex, out EquipOutcome? outcome, out uint wearLocation)
    {
        wearLocation = 0;
        var target = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
        if (target is null) { outcome = null; return null; }
        if (!items.TryGetValue(target.ItemId, out var definition) || definition is not IEquippableItemDefinition equippable
            || target.Equip != 0 || (equippable.EquipLocation & requestedPosition) == 0)
        {
            outcome = new EquipOutcome(EquipMutationResult.Fail, 0);
            return null;
        }

        var meta = definition.Meta;
        if (meta is not null)
        {
            // Pinned pc.cpp:1882-1885: elv/elvmax are independent gates, each only checked when non-zero.
            if ((meta.EquipLevelMin != 0 && characterBaseLevel < meta.EquipLevelMin) || (meta.EquipLevelMax != 0 && characterBaseLevel > meta.EquipLevelMax))
            {
                outcome = new EquipOutcome(EquipMutationResult.FailLevel, 0);
                return null;
            }
            if (meta.Sex != ItemMetadata.BothSexes && meta.Sex != characterSex)
            {
                outcome = new EquipOutcome(EquipMutationResult.Fail, 0);
                return null;
            }
        }
        if (target.Details.IsBroken)
        {
            outcome = new EquipOutcome(EquipMutationResult.Fail, 0);
            return null;
        }
        // shortcut: allow_equip_restricted_item (always-false default) is not modeled as a conf
        // toggle - this project enforces the restriction unconditionally, matching the pinned
        // DEFAULT battle_config value; upgrade once a conf-override concept exists for this flag.
        if (meta is { RestrictedOnNormalMaps: true })
        {
            outcome = new EquipOutcome(EquipMutationResult.Fail, 0);
            return null;
        }

        var pos = equippable.EquipLocation;
        if (pos == EquipSlots.Arms && equippable.EquipLocation == EquipSlots.Bitmask[EquipSlot.HandRight])
        {
            // Dual-wield-capable weapon (pinned pc.cpp:12108-12115): item's own possible
            // location is exactly EQP_HAND_R, but EQP_ARMS is offered as an alternative when
            // the caller requests both hands.
            pos = requestedPosition & EquipSlots.Arms;
            if (pos == EquipSlots.Arms)
            {
                var rightTaken = inventory.Items.Any(i => (i.Equip & EquipSlots.Bitmask[EquipSlot.HandRight]) != 0);
                var leftTaken = inventory.Items.Any(i => (i.Equip & EquipSlots.Bitmask[EquipSlot.HandLeft]) != 0);
                pos = (rightTaken && !leftTaken) ? EquipSlots.Bitmask[EquipSlot.HandLeft] : EquipSlots.Bitmask[EquipSlot.HandRight];
            }
        }

        // Slot conflict: unequip whatever currently occupies any EQI slot `pos` maps to (pinned pc.cpp:12147-12157). One mutation per
        // occupying row even when it spans several slots (a two-handed weapon).
        var mutations = new List<InventoryMutation>();
        var unequipped = new HashSet<uint>();
        foreach (var slot in EquipSlots.Bitmask)
        {
            if ((pos & slot.Value) == 0) continue;
            var occupying = inventory.Items.FirstOrDefault(i => i.SlotIndex != slotIndex && (i.Equip & slot.Value) != 0);
            if (occupying is null || !unequipped.Add(occupying.DurableId)) continue;
            var cleared = occupying.ToRow() with { Equip = 0 };
            mutations.Add(new UpdateRowMutation(InventoryContainer.Inventory, occupying.DurableId, occupying.Details.Revision, cleared));
        }

        mutations.Add(new UpdateRowMutation(InventoryContainer.Inventory, target.DurableId, target.Details.Revision, target.ToRow() with { Equip = pos }));
        outcome = new EquipOutcome(EquipMutationResult.Success, pos);
        wearLocation = pos;
        return mutations;
    }

    // Pinned pc_unequipitem (pc.cpp:12398-12495), restricted to this service's modeled checks:
    //   - not currently equipped (items.equip == 0) -> failure (pc.cpp:12407-12410)
    // Ammo-unequip-on-weapon-change and status-effect-cancel side effects are out of scope here.
    public async Task<(UnequipOutcome? Outcome, CharacterInventorySnapshot? Updated)> UnequipAsync(uint slotIndex, CancellationToken cancellationToken)
    {
        var current = inventoryService.Snapshot.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
        if (current is null || current.Equip == 0) return (new UnequipOutcome(false, 0), null);

        var position = current.Equip;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.Equip, inventory =>
        {
            var target = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
            return target is null || target.Equip == 0
                ? null
                : [new UpdateRowMutation(InventoryContainer.Inventory, target.DurableId, target.Details.Revision, target.ToRow() with { Equip = 0 })];
        }, cancellationToken);

        return change.Succeeded ? (new UnequipOutcome(true, position), change.Snapshot) : (new UnequipOutcome(false, 0), null);
    }
}
