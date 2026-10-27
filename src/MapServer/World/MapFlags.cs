using Athena.Net.MapServer.Generated.MapFlags;

namespace Athena.Net.MapServer.World;

// Static pinned map flags (GeneratedMapFlags, from the active npc graph's mapflag directives).
[Flags]
public enum MapFlag : ushort
{
    None = 0, Pvp = 1, Gvg = 2, GvgTe = 4, GvgDungeon = 8, GvgCastle = 16, GvgTeCastle = 32, Battleground = 64, NoDrop = 128, Restricted = 256,
}

public readonly record struct MapFlagSet(MapFlag Flags, uint Zone)
{
    public static MapFlagSet For(string map) => GeneratedMapFlags.ByMap.GetValueOrDefault(map);

    public bool NoDrop => Flags.HasFlag(MapFlag.NoDrop);

    // Pinned itemdb_isNoEquip (itemdb.cpp): mapdata_flag_vs2 (any PVP/GvG/BG type) decides "normal", then each typed bit, then the
    // zone bits when MF_RESTRICTED is set.
    public bool IsNoEquip(uint noEquip)
    {
        if (noEquip == 0) return false;
        var versus = (Flags & (MapFlag.Pvp | MapFlag.GvgDungeon | MapFlag.Gvg | MapFlag.GvgCastle | MapFlag.GvgTe | MapFlag.GvgTeCastle | MapFlag.Battleground)) != 0;
        return ((noEquip & 1) != 0 && !versus)
            || ((noEquip & 2) != 0 && Flags.HasFlag(MapFlag.Pvp))
            || ((noEquip & 4) != 0 && (Flags & (MapFlag.Gvg | MapFlag.GvgCastle)) != 0)
            || ((noEquip & 8) != 0 && Flags.HasFlag(MapFlag.Battleground))
            || ((noEquip & 16) != 0 && (Flags & (MapFlag.GvgTe | MapFlag.GvgTeCastle)) != 0)
            || ((noEquip & Zone) != 0 && Flags.HasFlag(MapFlag.Restricted));
    }
}
