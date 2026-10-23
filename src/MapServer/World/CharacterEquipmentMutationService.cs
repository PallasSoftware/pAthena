using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

// Pinned enum equip_index (pc.hpp:73-96) - the EQI_* slot array pc_equipitem/pc_unequipitem
// use to detect slot conflicts and swap out an already-equipped item. Every EQI slot is modeled,
// in pinned EQI order (pc_equipswitch walks slots in that order): the catalog carries costume and
// shadow-gear items, and an omitted slot would let two items occupy it at once.
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
    CostumeHeadTop,
    CostumeHeadMid,
    CostumeHeadLow,
    CostumeGarment,
    Ammo,
    ShadowArmor,
    ShadowWeapon,
    ShadowShield,
    ShadowShoes,
    ShadowAccRight,
    ShadowAccLeft,
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
        [EquipSlot.CostumeHeadTop] = 0x000400,
        [EquipSlot.CostumeHeadMid] = 0x000800,
        [EquipSlot.CostumeHeadLow] = 0x001000,
        [EquipSlot.CostumeGarment] = 0x002000,
        [EquipSlot.Ammo] = 0x008000,
        [EquipSlot.ShadowArmor] = 0x010000,
        [EquipSlot.ShadowWeapon] = 0x020000,
        [EquipSlot.ShadowShield] = 0x040000,
        [EquipSlot.ShadowShoes] = 0x080000,
        [EquipSlot.ShadowAccRight] = 0x100000,
        [EquipSlot.ShadowAccLeft] = 0x200000,
    };

    // Pinned EQP_ARMS = EQP_HAND_R|EQP_HAND_L (pc.hpp:1132).
    public const uint Arms = 0x000002 | 0x000020;

    // Pinned EQP_ACC = EQP_ACC_L|EQP_ACC_R (pc.hpp:1134).
    public const uint Acc = 0x000080 | 0x000008;

    // The first EQI index (pinned equip_index order) a worn mask occupies; int.MaxValue for a mask on no modeled slot.
    public static int EqiOrder(uint equip)
    {
        var index = 0;
        foreach (var mask in Bitmask.Values)
        {
            if ((equip & mask) != 0) return index;
            index++;
        }
        return int.MaxValue;
    }

    // Pinned EQP_SHADOW_ACC / EQP_SHADOW_ARMS (pc.hpp:1138-1139).
    public const uint ShadowAcc = 0x100000 | 0x200000;
    public const uint ShadowArms = 0x020000 | 0x040000;
}

// Pinned sd->equipswitch_tick (pc.hpp:549), armed by clif_parse_equipswitch_request (clif.cpp:22362-22367) with
// skill_get_cooldown(ALL_EQSWITCH, 1). Per session, never persisted, exactly like the pinned tick.
public sealed class EquipSwitchCooldown(TimeProvider timeProvider)
{
    // shortcut: pinned db/re/skill_db.yml ALL_EQSWITCH `Cooldown: 10000` - skill cooldowns are not yet in the generated
    // skill data; read it from there once the skill generator emits Cooldown.
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(10000);

    private DateTimeOffset _readyAt = DateTimeOffset.MinValue;
    private readonly Lock _gate = new();

    // False while the cooldown runs; otherwise arms it and returns true.
    public bool TryStart()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (now < _readyAt) return false;
            _readyAt = now + Duration;
            return true;
        }
    }
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
// pc_equipitem/pc_unequipitem (pc.cpp:12015-12240, 12398-12500) and the equip-switch loadout
// (pc_equipswitch/pc_equipswitch_remove, pc.cpp:12500-12599), scoped to the structural
// slot-conflict/EQP_ACC/EQP_ARMS position logic those functions implement plus pc_isequip's (pc.cpp:1850-1908)
// level/sex/broken/restricted-map eligibility gate and pc_job_can_use_item's (pc.cpp:1771-1790)
// class_base job-eligibility gate (GeneratedJobEquipClass.TryGet + ItemMetadata.ClassBase0/1/2).
// Explicitly NOT modeled (Feature 16 stage S4, documented as a tracked gap, not silently skipped):
// pc_isItemClass's ClassUpper/JOBL_UPPER|BABY|THIRD|FOURTH tier eligibility (a materially larger,
// separate bitmask mechanism than the base/2-1/2-2 class_base check - this project still does not
// model rAthena's JOBL_UPPER/BABY/THIRD/FOURTH tier bits at runtime, only the base-profession
// FirstMask/ClassBaseIndex pair GeneratedJobEquipClass bakes offline), status-effect equip-blocking
// (SC__STRIPACCESSORY etc. - Feature 17/status-effect territory), and ammo/weapon-type compatibility
// (ammo_check_weapon).
//
// CharServer remains the durable owner of CharInventory.Equip. An equip is ONE atomic inventory operation that unequips every
// conflicting item AND equips the target, so a failure can never leave the old item unequipped while the new one is not worn.
// The runtime snapshot is only replaced by the confirmed result of that operation (CharacterInventoryService), and success is
// only reported after it.
public sealed class CharacterEquipmentMutationService(CharacterInventoryService inventoryService, IStatRules statRules)
{
    // Pinned pc_equipitem/pc_unequipitem never leave a character wearing an item combination
    // status_calc_pc itself cannot resolve - the equip/unequip call IS what rebuilds
    // sd->inventory_data[]/sd->equip_index[] (pc.cpp:12230-12240, 12481-12488), so an item
    // the stat-calc layer cannot account for would already have failed earlier in the SAME
    // pinned function, never deferred to a later read. This project's EquipAsync/UnequipAsync
    // mirror that: the PROPOSED post-mutation inventory is validated through
    // IStatRules.TryResolveEquipment BEFORE the durable operation is sent, so a persisted equip
    // state can never later make TryGetCombatSnapshotAsync's own TryResolveEquipment call fail
    // and silently drop the player from every monster's valid-target set. A row the resolver
    // cannot account for (bonus4/5, an SP_* param outside the current vocabulary, an autobonus
    // whose own payload is unsupported, deferred scripts, an unmodeled enchant grade, ...) is
    // rejected outright (EquipMutationResult.Fail / UnequipOutcome(false, 0)), never persisted.
    // Validates both that the resulting inventory resolves (TryResolveEquipment) AND that the full
    // downstream status calculation the equip-time gate exists to protect - CalculateDerivedStats, the
    // same call TryGetCombatSnapshotAsync and every status-effect recalculation will make against this
    // exact EquipmentStats later - completes without throwing. TryResolveEquipment returning true is a
    // necessary but not sufficient condition for an equip to be safe: a resolvable EquipmentStats can
    // still be one CalculateDerivedStats cannot finish (e.g. a not-yet-modeled stat combination), and an
    // equip that fails only there would otherwise persist, then crash the very next status read instead
    // of this equip ack. Any such exception is treated identically to an unresolvable equip - reject,
    // never persist - rather than letting it propagate out of the equip-time validation.
    private bool CanResolveAfter(CharacterInventorySnapshot inventory, IReadOnlyList<InventoryMutation> mutations, IReadOnlyDictionary<int, ItemDefinition> items)
    {
        var rows = inventory.Items.ToDictionary(i => i.DurableId);
        foreach (var mutation in mutations)
            if (mutation is UpdateRowMutation { Container: InventoryContainer.Inventory } update && rows.TryGetValue(update.DurableId, out var existing))
                rows[update.DurableId] = CharacterInventoryItem.FromRow(update.NewRow, existing.SlotIndex);
        var simulated = new CharacterInventorySnapshot([.. rows.Values]);
        if (!statRules.TryResolveEquipment(simulated, items, out var equipment)) return false;
        try
        {
            var probeState = new CharacterGameplayState(CharacterId: 0, Version: 0, JobClass: 0, BaseLevel: 1, JobLevel: 1,
                BaseExperience: 0, JobExperience: 0, CurrentHp: 1, CurrentSp: 1, MaxHp: 1, MaxSp: 1, StatPoints: 0, SkillPoints: 0,
                Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);
            var probeSnapshot = new CharacterStatusSnapshot(probeState, new EffectiveCharacterStats(1, 1, 1, 1, 1, 1, 0, 0), DateTimeOffset.UnixEpoch);
            statRules.CalculateDerivedStats(probeSnapshot, equipment);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Pinned pc_equipitem (pc.cpp:12015-12172), restricted to this service's modeled checks, in pinned order:
    //   1. pc_isequip's own eligibility gate (pc.cpp:1850-1908): EquipLevelMin/Max -> FAILLEVEL,
    //      Sex mismatch -> FAIL, broken (Attribute != 0) -> FAIL, restricted-map -> FAIL (see
    //      ItemMetadata.RestrictedOnNormalMaps's own doc comment for this project's current
    //      "every reachable map is normal" scope).
    //   2. pc_job_can_use_item's class_base job-eligibility gate -> FAIL (pc.cpp:1771-1790,
    //      called from pc_equipitem:1961-1962 - AFTER pc_isequip's own checks, before the final
    //      OK return; see GeneratedJobEquipClass's own doc comment for the full pinned derivation).
    //   3. requested position must intersect pc_equippoint, and the row must not already be
    //      equipped -> ITEM_EQUIP_ACK_FAIL (pc.cpp:12063-12070)
    //   4. position resolution (pc.cpp:12088-12124): EQP_ACC picks ONE accessory slot (or the
    //      slot a compounded card forces), a dual-wield-capable weapon (pc_equippoint returning
    //      EQP_ARMS) picks one hand, shadow accessories/arms likewise
    //   5. slot conflict: any currently-equipped item occupying one of the resolved position's
    //      EQI slots is unequipped first (pc.cpp:12147-12157); the row leaves the equip-switch set
    //      (pc_equipswitch_remove, pc.cpp:12161) and a bind-on-equip item is bound (pc.cpp:12083-12086)
    // Returns a null outcome if the requested slotIndex is not a real row in the snapshot (the caller turns that into no ack,
    // matching pc_equipitem's own `if (!(id = sd->inventory_data[n])) return false;` early-out).
    public async Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> EquipAsync(
        uint slotIndex,
        uint requestedPosition,
        IReadOnlyDictionary<int, ItemDefinition> items,
        uint characterBaseLevel,
        byte characterSex,
        JobClass characterJobClass,
        CancellationToken cancellationToken)
    {
        var wearer = new Wearer(items, characterBaseLevel, characterSex, characterJobClass);
        return await MutateAsync(InventoryOperationKind.Equip, inventory =>
        {
            var work = new EquipWorkingSet(inventory);
            if (work.BySlot(slotIndex) is not { } target) return (null, null);
            var result = TryEquip(work, target, requestedPosition, wearer, toSwitch: false, out var pos);
            return (new EquipOutcome(result, result == EquipMutationResult.Success ? pos : 0), work);
        }, items, cancellationToken);
    }

    // ---- Equip switch (pinned feature.equipswitch, on by default: conf/battle/feature.conf:89) ----
    //
    // A prepared alternate loadout stored per row in InventoryItemRow.EquipSwitch (pinned item.equipSwitch, the EQP_* mask
    // the row will be worn at). The pinned sd->equip_switch_index[] cache is never stored separately here: it is derived from
    // the rows on every plan, exactly how pc_load rebuilds it (pc.cpp:1594-1602), so there is one durable owner (CharServer).
    // Every operation below is ONE atomic, idempotent inventory operation: the whole pinned step sequence is applied to a
    // working copy, then every touched row is sent as one batch.
    //
    // Deliberate divergence, stricter than pinned: pc_equipswitch removes a row from the switch set BEFORE calling
    // pc_equipitem and ignores its result, so a row that has become ineligible (level/class/broken) silently drops out of both
    // the loadout and the equipment. Here any rejected step rejects the whole switch - nothing changes, nothing is lost.

    // Pinned clif_parse_equipswitch_add (clif.cpp:22315-22341) -> pc_equipitem(sd, n, pos, equipswitch=true): the same
    // eligibility gate as a real equip, ammo refused, then the row is registered at the resolved position, displacing any
    // row already registered on one of its slots (pc.cpp:12135-12150). Nothing is worn, so no stat validation is needed.
    public Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> AddToEquipSwitchAsync(
        uint slotIndex, uint requestedPosition, IReadOnlyDictionary<int, ItemDefinition> items,
        uint characterBaseLevel, byte characterSex, JobClass characterJobClass, CancellationToken cancellationToken)
    {
        var wearer = new Wearer(items, characterBaseLevel, characterSex, characterJobClass);
        return MutateAsync(InventoryOperationKind.EquipSwitch, inventory =>
        {
            var work = new EquipWorkingSet(inventory);
            if (work.BySlot(slotIndex) is not { } target) return (null, null);
            var result = TryEquip(work, target, requestedPosition, wearer, toSwitch: true, out var pos);
            return (new EquipOutcome(result, result == EquipMutationResult.Success ? pos : 0), work);
        }, items, cancellationToken);
    }

    // Pinned clif_parse_equipswitch_remove (clif.cpp:22275-22293) -> pc_equipswitch_remove (pc.cpp:12580-12599). Removing a
    // row that is not registered is a no-op (pinned returns without an answer): Success=false, nothing sent.
    public async Task<(UnequipOutcome? Outcome, CharacterInventorySnapshot? Updated)> RemoveFromEquipSwitchAsync(uint slotIndex, CancellationToken cancellationToken)
    {
        var current = inventoryService.Snapshot.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
        if (current is null) return (null, null);
        if (current.Details.EquipSwitch == 0) return (new UnequipOutcome(false, 0), null);

        uint position = 0;
        var change = await inventoryService.ExecuteAsync(InventoryOperationKind.EquipSwitch, inventory =>
        {
            var work = new EquipWorkingSet(inventory);
            if (work.BySlot(slotIndex) is not { Details.EquipSwitch: not 0 } target) return null;
            position = target.Details.EquipSwitch;
            work.Set(target, equipSwitch: 0);
            return work.Mutations();
        }, cancellationToken);
        return change.Succeeded ? (new UnequipOutcome(true, position), change.Snapshot) : (new UnequipOutcome(false, 0), null);
    }

    // Pinned clif_parse_equipswitch_request (clif.cpp:22356-22389) -> skill ALL_EQSWITCH -> SkillEquipSwitch::castendNoDamageId
    // (skills/other/equipswitch.cpp): pc_equipswitch on every registered slot not already covered by an earlier swap. The
    // cooldown is armed before anything else is checked (clif.cpp:22362-22367), so a rejected or empty request still spends it.
    // Null outcome: the cooldown is running (pinned "client will not let you send a request") or nothing is registered
    // (pinned clif_equipswitch_reply + return) - nothing is sent either way.
    public Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> SwitchAllAsync(
        EquipSwitchCooldown cooldown, IReadOnlyDictionary<int, ItemDefinition> items,
        uint characterBaseLevel, byte characterSex, JobClass characterJobClass, CancellationToken cancellationToken)
    {
        if (!cooldown.TryStart()) return Task.FromResult<(EquipOutcome?, CharacterInventorySnapshot?)>((null, null));
        var wearer = new Wearer(items, characterBaseLevel, characterSex, characterJobClass);
        return MutateAsync(InventoryOperationKind.EquipSwitch, inventory =>
        {
            var work = new EquipWorkingSet(inventory);
            uint position = 0;
            foreach (var slot in EquipSlots.Bitmask.Values)
            {
                if ((position & slot) != 0 || work.Occupant(slot, switchSet: true) is not { } registered) continue;
                var result = Switch(work, registered, wearer, out var swapped);
                if (result != EquipMutationResult.Success) return (new EquipOutcome(result, 0), null);
                position |= swapped;
            }
            return position == 0 ? (null, null) : (new EquipOutcome(EquipMutationResult.Success, position), work);
        }, items, cancellationToken);
    }

    // Pinned clif_parse_equipswitch_request_single (clif.cpp:22391-22430): a registered row is swapped in on its own
    // (pc_equipswitch); an unregistered row is registered at its own pc_equippoint instead. No cooldown on this path.
    public Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> SwitchSingleAsync(
        uint slotIndex, IReadOnlyDictionary<int, ItemDefinition> items,
        uint characterBaseLevel, byte characterSex, JobClass characterJobClass, CancellationToken cancellationToken)
    {
        var wearer = new Wearer(items, characterBaseLevel, characterSex, characterJobClass);
        return MutateAsync(InventoryOperationKind.EquipSwitch, inventory =>
        {
            var work = new EquipWorkingSet(inventory);
            if (work.BySlot(slotIndex) is not { } target) return (null, null);
            if (target.Details.EquipSwitch != 0)
            {
                var switched = Switch(work, target, wearer, out var swapped);
                return (new EquipOutcome(switched, switched == EquipMutationResult.Success ? swapped : 0), work);
            }
            var point = items.TryGetValue(target.ItemId, out var definition) && definition is IEquippableItemDefinition equippable ? EquipPoint(equippable, characterJobClass) : 0;
            var result = TryEquip(work, target, point, wearer, toSwitch: true, out var pos);
            return (new EquipOutcome(result, result == EquipMutationResult.Success ? pos : 0), work);
        }, items, cancellationToken);
    }

    private readonly record struct Wearer(IReadOnlyDictionary<int, ItemDefinition> Items, uint BaseLevel, byte Sex, JobClass JobClass);

    // Runs one plan under the inventory gate. A successful plan that leaves someone WEARING something is validated through
    // CanResolveAfter before it is sent; a plan that only changes the switch set wears nothing new and skips that check.
    private async Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> MutateAsync(
        InventoryOperationKind kind, Func<CharacterInventorySnapshot, (EquipOutcome? Outcome, EquipWorkingSet? Work)> plan,
        IReadOnlyDictionary<int, ItemDefinition> items, CancellationToken cancellationToken)
    {
        EquipOutcome? decided = null;
        var unchanged = false;
        var change = await inventoryService.ExecuteAsync(kind, inventory =>
        {
            var (outcome, work) = plan(inventory);
            decided = outcome;
            if (outcome is not { Result: EquipMutationResult.Success } || work is null) return null;
            var mutations = work.Mutations();
            unchanged = mutations.Count == 0;
            if (work.WearsDifferently && !CanResolveAfter(inventory, mutations, items))
            {
                decided = new EquipOutcome(EquipMutationResult.Fail, 0);
                return null;
            }
            return mutations;
        }, cancellationToken);

        if (decided is null) return (null, null);
        if (decided.Value.Result != EquipMutationResult.Success) return (decided, null);
        // A successful plan with nothing to change (e.g. re-registering a row at the slot it already holds) is a success
        // with the unchanged snapshot - nothing was sent.
        if (unchanged) return (decided, change.Snapshot);
        return change.Succeeded ? (decided, change.Snapshot) : (new EquipOutcome(EquipMutationResult.Fail, 0), null);
    }

    // Pinned pc_equipswitch (pc.cpp:12500-12578) applied to the working set. Returns the swapped position mask (pinned return
    // value: the target's own mask, or that mask plus every unequipped row's mask).
    private static EquipMutationResult Switch(EquipWorkingSet work, CharacterInventoryItem target, Wearer wearer, out uint allPosition)
    {
        var position = target.Details.EquipSwitch;
        allPosition = position;

        // pc_checkequip(sd, position, true) == -1: nothing worn on any slot of the target mask (pc.cpp:7472-7490).
        if (!EquipSlots.Bitmask.Values.Any(slot => (position & slot) != 0 && work.Occupant(slot, switchSet: false) is not null))
        {
            work.Set(target, equipSwitch: 0);
            return TryEquip(work, work.Current(target), position, wearer, toSwitch: false, out _);
        }

        // Unequip everything worn on the target mask, remembering each row's own worn mask (pc.cpp:12517-12534).
        var unequipped = new List<(uint DurableId, uint Equip)>();
        uint unequippedPosition = 0;
        foreach (var slot in EquipSlots.Bitmask.Values)
        {
            if ((position & slot) == 0 || work.Occupant(slot, switchSet: false) is not { } worn) continue;
            unequipped.Add((worn.DurableId, worn.Equip));
            unequippedPosition |= worn.Equip;
            work.Set(worn, equip: 0);
        }
        allPosition = position | unequippedPosition;

        // Equip every registered row hit by the combined mask at its own registered position (pc.cpp:12538-12553).
        foreach (var slot in EquipSlots.Bitmask.Values)
        {
            if ((allPosition & slot) == 0 || work.Occupant(slot, switchSet: true) is not { } exchange) continue;
            var exchangePosition = exchange.Details.EquipSwitch;
            work.Set(exchange, equipSwitch: 0);
            var result = TryEquip(work, work.Current(exchange), exchangePosition, wearer, toSwitch: false, out _);
            if (result != EquipMutationResult.Success) return result;
        }

        // The rows taken off become the new loadout at the positions they were worn at (pc.cpp:12556-12575).
        foreach (var (durableId, equip) in unequipped) work.Set(work.Current(durableId), equipSwitch: equip);
        return EquipMutationResult.Success;
    }

    // Pinned pc_equipitem (pc.cpp:12015-12172) for both the worn equipment (toSwitch=false) and the equip-switch set
    // (toSwitch=true: pinned equip_index = sd->equip_switch_index, pc.cpp:12081), applied to the working set.
    private static EquipMutationResult TryEquip(EquipWorkingSet work, CharacterInventoryItem target, uint requestedPosition, Wearer wearer, bool toSwitch, out uint pos)
    {
        pos = 0;
        if (!wearer.Items.TryGetValue(target.ItemId, out var definition) || definition is not IEquippableItemDefinition equippable)
            return EquipMutationResult.Fail;
        if (CheckEligibility(target, definition, wearer) is { } rejected) return rejected;
        // Pinned pc.cpp:12059-12062: ammo is never registered for a switch. AmmoItemDefinition is not equippable through this
        // service at all yet (ammo_check_weapon is unmodeled), so the check above already refuses it on both paths.

        pos = EquipPoint(equippable, wearer.JobClass);
        if ((pos & requestedPosition) == 0 || target.Equip != 0) { pos = 0; return EquipMutationResult.Fail; }
        pos = ResolvePosition(work, target, pos, requestedPosition, equippable, wearer.Items, toSwitch);

        if (toSwitch)
        {
            // Pinned pc.cpp:12136-12146: a row already registered on one of these slots leaves the switch set entirely.
            foreach (var slot in EquipSlots.Bitmask.Values)
                if ((pos & slot) != 0 && work.Occupant(slot, switchSet: true) is { } registered && registered.DurableId != target.DurableId)
                    work.Set(registered, equipSwitch: 0);
            work.Set(work.Current(target), equipSwitch: pos);
            return EquipMutationResult.Success;
        }

        // Slot conflict: unequip whatever currently occupies any EQI slot `pos` maps to (pinned pc.cpp:12151-12157). One
        // mutation per occupying row even when it spans several slots (a two-handed weapon).
        foreach (var slot in EquipSlots.Bitmask.Values)
            if ((pos & slot) != 0 && work.Occupant(slot, switchSet: false) is { } occupying && occupying.DurableId != target.DurableId)
                work.Set(occupying, equip: 0);

        // Pinned pc.cpp:12083-12086: a bind-on-equip item becomes bound (battle_config.default_bind_on_equip, pinned conf 4 =
        // BOUND_CHAR) the first time it is worn.
        byte? bound = definition.Meta is { BindOnEquip: true } && target.Bound == 0 ? DefaultBindOnEquip : null;
        work.Set(work.Current(target), equip: pos, equipSwitch: 0, bound: bound);
        return EquipMutationResult.Success;
    }

    // Pinned conf/battle/items.conf default_bind_on_equip: 4 (BOUND_CHAR) - see the execution plan's pinned battle configuration.
    private const byte DefaultBindOnEquip = 4;

    private static EquipMutationResult? CheckEligibility(CharacterInventoryItem target, ItemDefinition definition, Wearer wearer)
    {
        var meta = definition.Meta;
        if (meta is not null)
        {
            // Pinned pc.cpp:1882-1885: elv/elvmax are independent gates, each only checked when non-zero.
            if ((meta.EquipLevelMin != 0 && wearer.BaseLevel < meta.EquipLevelMin) || (meta.EquipLevelMax != 0 && wearer.BaseLevel > meta.EquipLevelMax))
                return EquipMutationResult.FailLevel;
            if (meta.Sex != ItemMetadata.BothSexes && meta.Sex != wearer.Sex) return EquipMutationResult.Fail;
        }
        if (target.Details.IsBroken) return EquipMutationResult.Fail;
        // shortcut: allow_equip_restricted_item (always-false default) is not modeled as a conf
        // toggle - this project enforces the restriction unconditionally, matching the pinned
        // DEFAULT battle_config value; upgrade once a conf-override concept exists for this flag.
        if (meta is { RestrictedOnNormalMaps: true }) return EquipMutationResult.Fail;
        // Pinned pc_job_can_use_item (pc.cpp:1771-1790, called from pc_equipitem:1961-1962): tests
        // item_db's class_base[ClassBaseIndex] bit at FirstMask for the character's own job. A job
        // GeneratedJobEquipClass has no entry for (the "2" gender/appearance-variant ids - see that
        // generator's own doc comment) can never equip anything through this check, matching pinned
        // pc_authok's own invalid-class handling of the same condition.
        if (meta is not null)
        {
            if (!GeneratedJobEquipClass.TryGet(wearer.JobClass, out var equipClass)) return EquipMutationResult.Fail;
            var classBase = equipClass.ClassBaseIndex switch { 0 => meta.ClassBase0, 1 => meta.ClassBase1, _ => meta.ClassBase2 };
            if ((classBase & (1UL << equipClass.FirstMask)) == 0) return EquipMutationResult.Fail;
        }
        return null;
    }

    // Pinned pc_equippoint_sub (pc.cpp:1464-1483): a dagger/one-handed sword/one-handed axe whose own location is EQP_HAND_R
    // may be worn in either hand by a dual-wielding class - (class_ & MAPID_SECONDMASK) is MAPID_ASSASSIN or
    // MAPID_KAGEROUOBORO, which is exactly the (FirstMask, ClassBaseIndex) pair Thief(6)/2-1 or Ninja(9)/2-1 baked into
    // GeneratedJobEquipClass (every Assassin/Assassin Cross/Guillotine Cross/Shadow Cross/Kagerou/Oboro/... variant).
    // shortcut: the pc_checkskill(sd, AS_LEFT) alternative is not modeled (only the Assassin line learns it normally);
    // upgrade when a non-Assassin character can hold AS_LEFT.
    internal static uint EquipPoint(IEquippableItemDefinition item, JobClass jobClass)
    {
        var ep = item.EquipLocation;
        if (item is WeaponItemDefinition { WeaponType: WeaponType.Dagger or WeaponType.OneHandSword or WeaponType.OneHandAxe }
            && ep == EquipSlots.Bitmask[EquipSlot.HandRight]
            && GeneratedJobEquipClass.TryGet(jobClass, out var equipClass) && equipClass is { FirstMask: 6 or 9, ClassBaseIndex: 1 })
            return EquipSlots.Arms;
        return ep;
    }

    // Pinned pc.cpp:12088-12124: positions offered as "either of two slots" resolve to exactly one, looking at the worn
    // equipment (or the switch set, when registering a switch).
    private static uint ResolvePosition(EquipWorkingSet work, CharacterInventoryItem target, uint pos, uint requestedPosition,
        IEquippableItemDefinition item, IReadOnlyDictionary<int, ItemDefinition> items, bool switchSet)
    {
        bool Taken(EquipSlot slot) => work.Occupant(EquipSlots.Bitmask[slot], switchSet) is not null;
        uint Pick(uint pair, EquipSlot first, EquipSlot second, uint whenFirstTakenAndSecondFree, uint otherwise)
        {
            var picked = requestedPosition & pair;
            return picked == pair ? (Taken(first) && !Taken(second) ? whenFirstTakenAndSecondFree : otherwise) : picked;
        }

        if (pos == EquipSlots.Acc)
        {
            pos = Pick(EquipSlots.Acc, EquipSlot.AccRight, EquipSlot.AccLeft, EquipSlots.Bitmask[EquipSlot.AccLeft], EquipSlots.Bitmask[EquipSlot.AccRight]);
            // Accessories that have cards that force equip location (pc.cpp:12093-12107).
            var d = target.Details;
            var cards = (uint[])[d.Card0, d.Card1, d.Card2, d.Card3];
            for (var i = 0; i < Math.Min(((ItemDefinition)item).Meta?.Slots ?? 0, (uint)cards.Length); i++)
            {
                var cardId = cards[i];
                if (cardId != 0 && items.TryGetValue((int)cardId, out var card) && card is CardItemDefinition { EquipLocations: var cardPos }
                    && (cardPos == EquipSlots.Bitmask[EquipSlot.AccLeft] || cardPos == EquipSlots.Bitmask[EquipSlot.AccRight]))
                    return cardPos;
            }
            return pos;
        }
        if (pos == EquipSlots.Arms && item.EquipLocation == EquipSlots.Bitmask[EquipSlot.HandRight])
            // Dual-wield capable weapon, RENEWAL branch (pc.cpp:12108-12115).
            return Pick(EquipSlots.Arms, EquipSlot.HandRight, EquipSlot.HandLeft, EquipSlots.Bitmask[EquipSlot.HandLeft], EquipSlots.Bitmask[EquipSlot.HandRight]);
        if (pos == EquipSlots.ShadowAcc)
            return Pick(EquipSlots.ShadowAcc, EquipSlot.ShadowAccLeft, EquipSlot.ShadowAccRight, EquipSlots.Bitmask[EquipSlot.ShadowAccRight], EquipSlots.Bitmask[EquipSlot.ShadowAccLeft]);
        if (pos == EquipSlots.ShadowArms && item.EquipLocation == EquipSlots.Bitmask[EquipSlot.ShadowWeapon])
        {
            var picked = requestedPosition & EquipSlots.ShadowArms;
            return picked == EquipSlots.ShadowArms ? (Taken(EquipSlot.ShadowWeapon) ? EquipSlots.Bitmask[EquipSlot.ShadowShield] : EquipSlots.Bitmask[EquipSlot.ShadowWeapon]) : picked;
        }
        return pos;
    }

    // The rows one plan is computed over. Pinned pc_equipitem/pc_equipswitch mutate sd->inventory in place step by step;
    // this applies the same steps to a copy and emits one UpdateRowMutation per row whose durable columns changed, in the
    // order the rows were first touched, so the whole sequence is one atomic operation.
    private sealed class EquipWorkingSet(CharacterInventorySnapshot inventory)
    {
        private readonly List<CharacterInventoryItem> _rows = [.. inventory.Items];
        private readonly List<uint> _touched = [];

        public CharacterInventoryItem? BySlot(uint slotIndex) => _rows.FirstOrDefault(r => r.SlotIndex == slotIndex);
        public CharacterInventoryItem Current(CharacterInventoryItem row) => Current(row.DurableId);
        public CharacterInventoryItem Current(uint durableId) => _rows.First(r => r.DurableId == durableId);

        // Pinned sd->equip_index[i] / sd->equip_switch_index[i] for one EQI slot mask.
        public CharacterInventoryItem? Occupant(uint slot, bool switchSet) =>
            _rows.FirstOrDefault(r => ((switchSet ? r.Details.EquipSwitch : r.Equip) & slot) != 0);

        public void Set(CharacterInventoryItem row, uint? equip = null, uint? equipSwitch = null, byte? bound = null)
        {
            var index = _rows.FindIndex(r => r.DurableId == row.DurableId);
            var current = _rows[index];
            _rows[index] = current with
            {
                Equip = equip ?? current.Equip,
                Bound = bound ?? current.Bound,
                Detail = current.Details with { EquipSwitch = equipSwitch ?? current.Details.EquipSwitch },
            };
            if (!_touched.Contains(row.DurableId)) _touched.Add(row.DurableId);
        }

        // True when a worn mask changed: the plan changes what the character wears, so stats must be re-validated.
        public bool WearsDifferently => _touched.Any(id => Original(id).Equip != Current(id).Equip);

        public IReadOnlyList<InventoryMutation> Mutations() =>
        [
            .. _touched
                .Select(id => (Original: Original(id), Now: Current(id)))
                .Where(p => p.Original.Equip != p.Now.Equip || p.Original.Bound != p.Now.Bound || p.Original.Details.EquipSwitch != p.Now.Details.EquipSwitch)
                .Select(p => new UpdateRowMutation(InventoryContainer.Inventory, p.Original.DurableId, p.Original.Details.Revision, p.Now.ToRow())),
        ];

        private CharacterInventoryItem Original(uint durableId) => inventory.Items.First(r => r.DurableId == durableId);
    }

    // Pinned pc_unequipitem (pc.cpp:12398-12495), restricted to this service's modeled checks:
    //   - not currently equipped (items.equip == 0) -> failure (pc.cpp:12407-12410)
    // Ammo-unequip-on-weapon-change and status-effect-cancel side effects are out of scope here.
    // No CanResolveAfter check needed here (unlike EquipAsync): clearing one row's Equip bit can
    // only REMOVE that row from TryResolveEquipment's iteration, never newly introduce a resolver
    // failure that wasn't already present before the unequip.
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
