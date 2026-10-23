using Athena.Net.MapServer.World.Items;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

public sealed class ItemPackTests
{
    private const string Commit = "e985006171d2eb320ee512a653f4c83aea3d81b6";

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static RathenaConstantTable Constants() => RathenaConstantTable.Load(File.ReadAllText(Path.Combine(Root(), "tools/WorldDataImporter/rathena-constants.tsv")));
    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), "legacy/rathena", relative));

    private static readonly Lazy<ItemPackBuilder.BuildResult> Built = new(() => ItemPackBuilder.Build(Read, Constants(), Commit));

    [Fact]
    public void Pack_RegenerationIsByteIdenticalToTheCommittedPack()
    {
        var committed = File.ReadAllBytes(Path.Combine(Root(), "src/MapServer/Generated/Assets/Items/AthenaItems.bin"));
        Assert.Equal(committed, ItemPackFormat.Write(Built.Value.Pack));
    }

    [Fact]
    public void Pack_FacadeAndReportAreReproducible()
    {
        var facade = File.ReadAllText(Path.Combine(Root(), "src/MapServer/Generated/GameData/Items/GeneratedItems.cs"));
        Assert.Equal(facade, ItemPackBuilder.RenderFacade(Built.Value.Pack, Commit).Replace("\r\n", "\n", StringComparison.Ordinal));
        var report = File.ReadAllText(Path.Combine(Root(), "src/MapServer/Generated/Assets/Items/AthenaItems.report.txt"));
        Assert.Equal(report, Built.Value.Report);
    }

    [Fact]
    public void Pack_RoundTripsEveryItemAndScript()
    {
        var bytes = ItemPackFormat.Write(Built.Value.Pack);
        var read = ItemPackFormat.Read(bytes);
        Assert.Equal(Commit, read.RathenaCommit);
        Assert.Equal(Built.Value.Pack.Items, read.Items);
        Assert.Equal(Built.Value.Pack.Scripts.Count, read.Scripts.Count);
        Assert.Equal(Built.Value.Pack.Groups.Count, read.Groups.Count);
        Assert.Equal(Built.Value.Pack.Groups.Sum(g => g.SubGroups.Sum(s => s.Entries.Count)), read.Groups.Sum(g => g.SubGroups.Sum(s => s.Entries.Count)));
        Assert.Equal(Built.Value.Pack.Groups.SelectMany(g => g.SubGroups).SelectMany(s => s.Entries), read.Groups.SelectMany(g => g.SubGroups).SelectMany(s => s.Entries));
        for (var i = 0; i < read.Scripts.Count; i++)
        {
            var expected = Built.Value.Pack.Scripts[i];
            var actual = read.Scripts[i];
            Assert.Equal(expected.Source, actual.Source);
            Assert.Equal(expected.Support, actual.Support);
            Assert.Equal(expected.DeferredConstruct, actual.DeferredConstruct);
            Assert.Equal(expected.Uses, actual.Uses);
            Assert.True(SameTree(expected.Program, actual.Program), $"script {i} IR changed in the round trip");
        }
    }

    [Fact]
    public void Pack_RejectsTamperedContent()
    {
        var bytes = ItemPackFormat.Write(Built.Value.Pack);
        bytes[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => ItemPackFormat.Read(bytes));
    }

    [Fact]
    public void Scripts_EveryDistinctScriptIsLoweredOrDeferredWithAnOwner()
    {
        var scripts = Built.Value.Pack.Scripts;
        Assert.All(scripts, script =>
        {
            if (script.Support == PackScriptSupport.Lowered) { Assert.Null(script.DeferredConstruct); Assert.NotNull(script.Program); }
            else { Assert.False(string.IsNullOrEmpty(script.DeferredConstruct)); Assert.NotEqual(ItemScriptOwner.Item, script.DeferredOwner); }
        });
        // Nothing may be deferred as "unknown": every command is either cataloged to an owner or reported as a generation failure.
        var unknown = scripts.Where(script => script.Support == PackScriptSupport.Deferred && script.DeferredOwner == ItemScriptOwner.Unknown).Select(script => script.DeferredConstruct).ToArray();
        Assert.True(unknown.Length == 0, "uncataloged constructs: " + string.Join(", ", unknown));
        // Deferred scripts are the general-script surface (callfunc, setarray, persistent variables) owned by Feature 20.
        Assert.InRange(scripts.Count(script => script.Support == PackScriptSupport.Deferred), 1, 100);
    }

    [Fact]
    public void Scripts_EveryItemScriptSlotPointsAtItsOwnSourceText()
    {
        var pack = Built.Value.Pack;
        var loaded = ItemDatabaseLoader.Load(Read, Constants()).Items.ToDictionary(item => item.Item.Id);
        Assert.Equal(loaded.Count, pack.Items.Count);
        foreach (var item in pack.Items)
        {
            var source = loaded[item.Id];
            Assert.Equal(source.Script, item.ScriptIndex < 0 ? null : pack.Scripts[item.ScriptIndex].Source);
            Assert.Equal(source.EquipScript, item.EquipScriptIndex < 0 ? null : pack.Scripts[item.EquipScriptIndex].Source);
            Assert.Equal(source.UnEquipScript, item.UnEquipScriptIndex < 0 ? null : pack.Scripts[item.UnEquipScriptIndex].Source);
        }
        // The pinned equipment/usable/etc files hold thousands of scripts; the pack must not have lost the bulk of them.
        Assert.True(pack.Items.Count(item => item.ScriptIndex >= 0) > 15_000);
    }

    private static bool SameTree(IrNode? a, IrNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Kind == b.Kind && a.Text == b.Text && a.Number == b.Number && a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(pair => SameTree(pair.First, pair.Second));
    }
}
