namespace Athena.Net.MapServer.World.Items;

// The pinned item loader's full record for one item plus its scripts, attached to every catalog-built ItemDefinition.
// Plain data: restrictions and effects are interpreted by the regional rules (Iro*/Kro*), never here.
public sealed record ItemMetadata(PackItem Data, PackScript? Script, PackScript? EquipScript, PackScript? UnEquipScript)
{
    public uint Weight => Data.Weight;
    public uint Slots => Data.Slots;
    public int Buy => Data.Buy;
    public int Sell => Data.Sell;
    public bool Refineable => Data.Flags.HasFlag(PackItemFlags.Refineable);
    public bool Gradable => Data.Flags.HasFlag(PackItemFlags.Gradable);

    // Pinned item_db Delay.Duration/Delay.Status (itemdb.cpp:762-801), gating pc_itemcd_check (pc.cpp:14766-14785). A
    // Status of SC_NONE (0) means this item has no shared cooldown group - DelayDuration alone is meaningless without it,
    // matching pc_itemcd_check's own fallback to the per-item-index delay list (pc_itemcd_add) when delay.sc is unset.
    // DelayGroupKey is the SC_ constant that rAthena uses as the shared-cooldown dedup key: two different items with the
    // same DelayGroupKey (e.g. all Blue Potions/White Potions under one healing-group status) share one running cooldown.
    public bool HasDelayGroup => Data.DelayStatus >= 0 && Data.DelayDuration > 0;
    public int DelayGroupKey => Data.DelayStatus;
    public uint DelayDurationMilliseconds => Data.DelayDuration;

    // Data.NoEquip itself (PackItem.NoEquip) carries the COMPLETE, unfiltered db/re/item_noequip.txt bitmask for this item -
    // every bit (1 normal, 2 PVP, 4 GvG, 8 Battleground, 16 WOE:TE, 32+ zones) survives the loader/pack pipeline verbatim.
    // Nothing is dropped or pre-filtered at the data layer; only the RUNTIME ENFORCEMENT below is scoped.
    //
    // Pinned itemdb_isNoEquip (itemdb.cpp:4442-4457): bit 1 ("restricted in normal maps", i.e. NOT PVP/GvG/Battleground/
    // WOE:TE) is the only bit this accessor currently evaluates, because this project has no PVP/GvG/Battleground/WOE:TE/
    // zone map-flag RUNTIME state yet - every map this project can currently place a character on is a "normal" map in
    // rAthena's own sense, so bit 1 alone is already a sound, complete check for every reachable map today.
    // KNOWN SCOPE GAP: bits 2/4/8/16/32+ are loaded into Data.NoEquip but NOT enforced by this accessor or the
    // HandleIroUseItemRequestAsync gate - an item restricted only in GvG/WOE/a zone will incorrectly be USABLE everywhere
    // right now, because this project cannot yet evaluate "is this map GvG/WOE/zone N". Upgrade this accessor (and the
    // gate) to read the full Data.NoEquip bitmask once that map-flag/zone runtime concept exists - do not special-case
    // any item to compensate; extend the generic bitmask evaluation against the new map-flag state instead.
    public bool RestrictedOnNormalMaps => (Data.NoEquip & 1) != 0;
}
