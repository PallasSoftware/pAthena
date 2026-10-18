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
}
