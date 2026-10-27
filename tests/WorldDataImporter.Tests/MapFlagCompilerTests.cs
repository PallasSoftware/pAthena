using Athena.WorldCompiler.Generation;
using static Athena.WorldCompiler.Generation.MapFlagCompiler;

// Pinned npc_parse_mapflag + map_setmapflag_sub semantics, in load order.
public sealed class MapFlagCompilerTests
{
    private static SortedDictionary<string, MapState> Run(params string[] files) =>
        MapFlagCompiler.Compile(files.Select((text, i) => ($"npc/f{i}.txt", text)));

    [Fact]
    public void LaterPvp_ClearsEveryGvgAndBattlegroundFlag_AndLaterGvgClearsPvp()
    {
        var maps = Run("a\tmapflag\tgvg_castle\nb\tmapflag\tpvp\n", "a\tmapflag\tpvp\nb\tmapflag\tgvg\n");
        Assert.Equal(Flag.Pvp, maps["a"].Flags);
        Assert.Equal(Flag.Gvg, maps["b"].Flags);
    }

    [Fact]
    public void Off_ClearsAFlag_AndRestrictedOffResetsEveryZone()
    {
        var maps = Run("a\tmapflag\tnodrop\na\tmapflag\trestricted\t1\na\tmapflag\trestricted\t7\nb\tmapflag\tnodrop\n",
            "a\tmapflag\trestricted\toff\nb\tmapflag\tnodrop\toff\n");
        Assert.Equal(new MapState(Flag.NoDrop, 0), maps["a"]);
        Assert.False(maps.ContainsKey("b"));
    }

    [Fact]
    public void RestrictedZones_Accumulate_AsPinnedBits_AndAMissingZoneIsSkipped()
    {
        var maps = Run("a\tmapflag\trestricted\t1\na\tmapflag\trestricted\t7\na\tmapflag\trestricted\n");
        Assert.Equal(new MapState(Flag.Restricted, 32u | 2048u), maps["a"]); // (1 << (z + 1)) << 3
    }

    [Fact]
    public void CommentedAndUnrelatedLines_AreIgnored()
    {
        var maps = Run("//a\tmapflag\tpvp\n/* b\tmapflag\tpvp\n*/\nc\tmapflag\tnomemo\nd,1,1,0\tscript\tX\t-1,{ end; }\n");
        Assert.Empty(maps);
    }

    [Fact]
    public void CheckedInGeneratedMapFlags_MatchACompileOfThePinnedActiveNpcGraph()
    {
        var repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repository, "Athena.NET.slnx"))) repository = Path.GetDirectoryName(repository)!;
        var root = Path.Combine(repository, "legacy/rathena");
        var files = RathenaScriptConfigGraph.ResolveActiveNpcFiles(root).Select(relative => (relative, File.ReadAllText(Path.Combine(root, relative))));
        var emitted = MapFlagCompiler.Emit(MapFlagCompiler.Compile(files), "e985006171d2eb320ee512a653f4c83aea3d81b6");
        Assert.Equal(emitted, File.ReadAllText(Path.Combine(repository, "src/MapServer/Generated/MapFlags/GeneratedMapFlags.cs")));
    }
}
