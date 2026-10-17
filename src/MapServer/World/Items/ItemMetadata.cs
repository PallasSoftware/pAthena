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
}
