using Athena.Net.MapServer.World;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

public sealed class MobPackTests
{
    private const string Commit = "e985006171d2eb320ee512a653f4c83aea3d81b6";
    private const string SourceFile = "legacy/rathena/db/re/mob_db.yml";

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly Lazy<string> PinnedYaml = new(() => File.ReadAllText(Path.Combine(Root(), SourceFile)));

    // The generate-mobs pack step: effective definitions (pinned + custom) -> symbols (duplicate-id validation) -> pack.
    private static (IReadOnlyList<MobDataCompiler.GeneratedMobSymbol> Symbols, MobPack Pack) Build(IReadOnlyList<(string, string)>? custom = null)
    {
        var symbols = MobDataCompiler.CreateGeneratedSymbols(MobDataCompiler.ReadAllMobDefinitions(PinnedYaml.Value, custom));
        return (symbols, new MobPack(Commit, symbols.Select(item => MobDataCompiler.ToMobDefinition(item.Mob, Commit, SourceFile)).ToArray()));
    }

    [Fact]
    public void Pack_RegenerationIsByteIdenticalToTheCommittedPack()
    {
        var committed = File.ReadAllBytes(Path.Combine(Root(), "src/MapServer/Generated/Assets/Mobs/AthenaMobs.bin"));
        Assert.Equal(committed, MobPackFormat.Write(Build().Pack));
    }

    [Fact]
    public void Pack_WithCustomLayer_IsByteDeterministic()
    {
        IReadOnlyList<(string, string)> custom = [("custom/mobs/a.yml", "Body:\n  - Id: 1002\n    Hp: 77\n  - Id: 900300\n    AegisName: CUSTOM_PACK\n    Name: Custom Pack\n")];
        var first = MobPackFormat.Write(Build(custom).Pack);

        Assert.Equal(first, MobPackFormat.Write(Build(custom).Pack));
        var reread = MobPackFormat.Read(first).Mobs;
        Assert.Equal(77u, reread.Single(mob => mob.Id == 1002).MaxHp);
        Assert.Equal(new Athena.Net.MapServer.World.WorldSourceInfo("Athena.NET", "", "custom/mobs/a.yml", 4), reread.Single(mob => mob.Id == 900300).Source);
    }
}
