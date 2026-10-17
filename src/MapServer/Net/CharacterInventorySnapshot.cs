using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.Net;

// One authoritative CharInventory row, as needed by the client-facing inventory/equip-list
// packets (0x0B09/0x0B39) and by CharacterEquipmentSnapshot's right-hand derivation. Fields
// mirror what clif_item_normal/clif_item_equip actually read from the persisted `item` struct
// (clif.cpp:2932-2960, 2987-3011) - Cards/Options are left unmodeled until a traced item needs
// them (the tutorial starter rows carry none), matching the shallow-extension convention used
// throughout this domain model.
//
// DurableId vs SlotIndex - the two are DELIBERATELY separate concepts, never conflated:
//   DurableId is CharInventory's own real primary key (CharInventory.Id) - the ONE stable
//   identity a row keeps for its entire persisted lifetime. CharServer is the sole authority
//   over it; MapServer never invents or reassigns it.
//
//   SlotIndex is the CURRENT SESSION's runtime inventory-array position (0-based), matching
//   pinned rAthena's sd->inventory.u.items_inventory[n] - entirely a MapServer-owned, in-memory
//   concept for THIS session. Pinned pc_delitem (pc.cpp:6103-6128) proves the real client-facing
//   behavior this must mirror: deleting a row does NOT compact/renumber later array positions -
//   it leaves a hole at that exact index, and pinned pc_additem (pc.cpp:...) proves a later add
//   searches the array for the first empty slot and reuses it, rather than always appending.
//   CharServer has no runtime-slot concept at all: it is never asked for one and never computes
//   one - see ai/map-server.md "Durable row identity vs runtime SlotIndex" for the full
//   architecture rationale and CharacterInventorySnapshot's own mutation-helper doc comments for
//   how MapServer maintains this session-local assignment (initial dense login mapping, hole
//   creation on delete, hole reuse on add, unchanged-slot preservation on stack/equip updates).
//
// client_index() (clif.cpp:122) adds +2 to SlotIndex at wire-serialization time; callers must
// never apply that offset themselves before this point.
public sealed record CharacterInventoryItem(
    uint DurableId,
    uint SlotIndex,
    int ItemId,
    uint Amount,
    uint Equip,
    bool Identified,
    byte Refine,
    byte Favorite,
    byte Bound,
    ItemInstanceDetail? Detail = null)
{
    // Everything else the durable row carries (cards, random options, broken flag, expiry, unique id, equip-switch, enchant grade) and
    // the row revision CharServer uses to reject stale writers. Never null-checked by callers: a row built without details has the
    // all-zero default (no cards/options, not broken, revision 0).
    public ItemInstanceDetail Details => Detail ?? ItemInstanceDetail.Empty;

    internal static CharacterInventoryItem FromRow(InventoryItemRow row, uint slotIndex) => new(
        row.DurableId, slotIndex, (int)row.ItemId, row.Amount, row.Equip, row.Identify != 0, row.Refine, row.Favorite, row.Bound,
        new ItemInstanceDetail(row.Attribute, row.EnchantGrade, row.Card0, row.Card1, row.Card2, row.Card3, row.Option0, row.Option1, row.Option2, row.Option3, row.Option4,
            row.ExpireTime, row.UniqueId, row.EquipSwitch, row.Revision));

    // The durable row this runtime row mirrors, used as the starting point of an Update mutation (callers change the field they own).
    internal InventoryItemRow ToRow()
    {
        var d = Details;
        return new(DurableId, (uint)ItemId, Amount, Equip, Identified ? (byte)1 : (byte)0, Refine, d.Attribute, Favorite, Bound, d.EnchantGrade,
            d.Card0, d.Card1, d.Card2, d.Card3, d.Option0, d.Option1, d.Option2, d.Option3, d.Option4, d.ExpireTime, d.UniqueId, d.EquipSwitch, d.Revision);
    }
}

// Durable per-instance item data beyond the fields the client list needs on every packet.
public sealed record ItemInstanceDetail(
    byte Attribute,
    byte EnchantGrade,
    uint Card0, uint Card1, uint Card2, uint Card3,
    InventoryOption Option0, InventoryOption Option1, InventoryOption Option2, InventoryOption Option3, InventoryOption Option4,
    uint ExpireTime,
    ulong UniqueId,
    uint EquipSwitch,
    uint Revision)
{
    public static readonly ItemInstanceDetail Empty = new(0, 0, 0, 0, 0, 0, default, default, default, default, default, 0, 0, 0, 0);

    public bool IsBroken => Attribute != 0;
}

// Every persisted CharInventory row for one character, carrying MapServer's own runtime
// SlotIndex assignment for the active session (see CharacterInventoryItem's own doc comment for
// the DurableId/SlotIndex split). This is the ONE authoritative snapshot - both the right-hand
// weapon-appearance/combat path (CharacterEquipmentSnapshot, derived from this) and the full
// client inventory/equip-list projection (0x0B09/0x0B39) originate from the same fetch, never
// two independent CharServer reads of the same persisted state.
public sealed record CharacterInventorySnapshot(IReadOnlyList<CharacterInventoryItem> Items)
{
    // The initial dense runtime-slot assignment performed once, right after a successful
    // CharServer inventory read (login or reconnect) - CharServer's own stable enumeration
    // order (CharInventoryOrdering.InStableOrder) becomes slots 0..N-1. This is the ONLY place
    // runtime slots are assigned from CharServer's row order; every later mutation (add/
    // consume/equip) maintains the assignment purely in MapServer's own runtime state via
    // WithUpdatedItem/WithNewItem/WithoutDurableId below - CharServer is never asked to
    // recompute a slot again for the lifetime of this session.
    // Login/reload from the full durable rows: CharServer's stable order becomes dense runtime slots 0..N-1.
    internal static CharacterInventorySnapshot FromRows(IReadOnlyList<InventoryItemRow> rows) =>
        new([.. rows.Select((row, slot) => CharacterInventoryItem.FromRow(row, (uint)slot))]);

    // Applies the per-mutation results of one confirmed durable operation: a deleted row leaves a hole (pc_delitem), a known row keeps
    // its runtime slot, a new row takes the first free slot (pc_additem). Throws on a result that contradicts this snapshot (e.g. an
    // AddNew result for a DurableId already tracked): the service treats that as a lost-sync condition and reloads.
    internal CharacterInventorySnapshot Apply(IReadOnlyList<InventoryMutationResult> results)
    {
        var items = Items.ToList();
        foreach (var result in results)
        {
            var index = items.FindIndex(i => i.DurableId == result.DurableId);
            if (result.Deleted)
            {
                if (index < 0) throw new InvalidOperationException($"Inventory invariant violation: no runtime row for DurableId={result.DurableId} to delete.");
                items.RemoveAt(index);
            }
            else if (index >= 0)
            {
                items[index] = CharacterInventoryItem.FromRow(result.Row!, items[index].SlotIndex);
            }
            else
            {
                var occupied = items.Select(i => i.SlotIndex).ToHashSet();
                uint slot = 0;
                while (occupied.Contains(slot)) slot++;
                items.Add(CharacterInventoryItem.FromRow(result.Row!, slot));
            }
        }
        return new CharacterInventorySnapshot(items);
    }

    public static CharacterInventorySnapshot FromLogin(IReadOnlyList<(uint DurableId, int ItemId, uint Amount, uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound)> rows)
    {
        var items = new List<CharacterInventoryItem>(rows.Count);
        for (var slot = 0; slot < rows.Count; slot++)
        {
            var row = rows[slot];
            items.Add(new CharacterInventoryItem(row.DurableId, (uint)slot, row.ItemId, row.Amount, row.Equip, row.Identified, row.Refine, row.Favorite, row.Bound));
        }
        return new CharacterInventorySnapshot(items);
    }

    // Applies a confirmed-persisted mutation to an EXISTING row, identified by its DurableId -
    // used by CharacterEquipmentMutationService (equip/unequip: the row already exists, ItemId/
    // slot unchanged) and by a stack-amount update for an item the caller already knows is not
    // new (CharServer's own IsNewRow=false response). The row's CURRENT runtime SlotIndex is
    // looked up by DurableId and preserved unconditionally - this is the exact mechanism that
    // keeps a later, unrelated row's slot stable across an earlier row's deletion/replacement.
    // Throws if no row with this DurableId exists in the current snapshot - CharServer's
    // response disagreeing with this session's own runtime state is an authoritative-state
    // invariant violation, never silently guessed at or repaired.
    public CharacterInventorySnapshot WithUpdatedItem(uint durableId, int itemId, uint amount, uint equip, bool identified, byte refine, byte favorite, byte bound)
    {
        var index = Items.ToList().FindIndex(i => i.DurableId == durableId);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Inventory invariant violation: no runtime row exists for DurableId={durableId} to update.");
        }

        var items = Items.ToList();
        items[index] = items[index] with { ItemId = itemId, Amount = amount, Equip = equip, Identified = identified, Refine = refine, Favorite = favorite, Bound = bound };
        return new CharacterInventorySnapshot(items);
    }

    // Applies a confirmed-persisted BRAND-NEW row (CharServer's own IsNewRow=true response) -
    // assigns it the first FREE runtime slot: mirrors pinned pc_additem's own "search the array
    // for the first empty slot and reuse it" behavior (a hole left by an earlier WithoutDurableId
    // call), falling back to appending a new slot only when there is no hole to reuse. Throws if
    // a row with this DurableId already exists - a "new row" response for an already-tracked
    // DurableId is an authoritative-state invariant violation (CharServer and this session's
    // runtime state disagree on whether the row is new), never silently repaired.
    public CharacterInventorySnapshot WithNewItem(uint durableId, int itemId, uint amount, uint equip, bool identified, byte refine, byte favorite, byte bound)
    {
        if (Items.Any(i => i.DurableId == durableId))
        {
            throw new InvalidOperationException(
                $"Inventory invariant violation: DurableId={durableId} was reported as a new row, but a runtime row for it already exists.");
        }

        var occupiedSlots = Items.Select(i => i.SlotIndex).ToHashSet();
        uint slot = 0;
        while (occupiedSlots.Contains(slot)) slot++;

        var newItem = new CharacterInventoryItem(durableId, slot, itemId, amount, equip, identified, refine, favorite, bound);
        return new CharacterInventorySnapshot([.. Items, newItem]);
    }

    // Removes the row identified by its DurableId (a confirmed CharServer row deletion -
    // InventoryConsumePersistenceResult.RowDeleted). Deliberately leaves a HOLE at that row's
    // runtime SlotIndex rather than compacting/renumbering later rows - this is the pinned
    // pc_delitem behavior this architecture exists to mirror (pc.cpp:6114-6119: the array slot
    // is zeroed in place, never shifted). A later WithNewItem call may reuse this exact hole.
    // Throws if no row with this DurableId exists in the current snapshot.
    public CharacterInventorySnapshot WithoutDurableId(uint durableId)
    {
        if (!Items.Any(i => i.DurableId == durableId))
        {
            throw new InvalidOperationException(
                $"Inventory invariant violation: no runtime row exists for DurableId={durableId} to remove.");
        }

        return new CharacterInventorySnapshot([.. Items.Where(i => i.DurableId != durableId)]);
    }
}

// Pinned EQP_HAND_R = 0x000002 (mmo.hpp:340) - the only equip slot the weapon-appearance/combat
// path needs. RightHandItemId == null means "confirmed no right-hand item equipped" - derived
// from a successful CharacterInventorySnapshot read, so never ambiguous with a failed read (see
// CharacterInventoryReadResult).
public sealed record CharacterEquipmentSnapshot(int? RightHandItemId, byte RightHandRefine)
{
    internal static CharacterEquipmentSnapshot FromInventory(CharacterInventorySnapshot inventory)
    {
        // Pinned mmo.hpp:322: "equip; // location(s) where item is equipped (using enum
        // equip_pos for bitmasking)" - always tested via `equip & EQP_HAND_R`, never exact
        // equality (pc.cpp:1582/12173/12427), since a row's Equip can carry multiple
        // simultaneous position bits.
        var rightHand = inventory.Items.FirstOrDefault(item => (item.Equip & 0x000002) != 0);
        return rightHand is null
            ? new CharacterEquipmentSnapshot(null, 0)
            : new CharacterEquipmentSnapshot(rightHand.ItemId, rightHand.Refine);
    }
}

// A failed/unavailable inventory read (DB error, disconnected CharServer, malformed response)
// must never be represented the same way as "successfully confirmed empty/unarmed" -
// collapsing both into a nullable snapshot would let future combat/appearance code silently
// treat an unknown state as empty. Succeeded=false always carries Snapshot=null; Succeeded=true
// always carries a non-null Snapshot (Items may itself be empty - that is the authoritative
// "no inventory rows" case).
public readonly record struct CharacterInventoryReadResult(bool Succeeded, CharacterInventorySnapshot? Snapshot)
{
    public static CharacterInventoryReadResult Success(CharacterInventorySnapshot snapshot) => new(true, snapshot);
    public static CharacterInventoryReadResult Failed() => new(false, null);
}
