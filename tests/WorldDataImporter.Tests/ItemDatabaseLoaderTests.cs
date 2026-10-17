using System.IO.Compression;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

public sealed class ItemDatabaseLoaderTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static RathenaConstantTable Constants() => RathenaConstantTable.Load(File.ReadAllText(Path.Combine(Root(), "tools/WorldDataImporter/rathena-constants.tsv")));

    private static ItemDatabaseLoadResult LoadPinned() =>
        ItemDatabaseLoader.Load(relative => File.ReadAllText(Path.Combine(Root(), "legacy/rathena", relative)), Constants());

    [Fact]
    public void Yaml_ReadsInlineMapsBlockScalarsAndComments()
    {
        const string source = """
            Header:
              Type: ITEM_DB
            Body:
              - Id: 1100 # note
                Name: "[Event] Box"
                Jobs:
                  Novice: true
                Script: |
                  bonus bStr,1; // c
                  if (1) {
                    bonus bAgi,2;
                  }
              - Id: 1101
                Tags:
                - A
                - B
            """;
        var root = (YamlMap)RathenaYaml.Parse(source, "test.yml");
        var body = Assert.IsType<YamlSequence>(root.Get("Body"));
        var first = Assert.IsType<YamlMap>(body.Items[0]);
        Assert.Equal("1100", first.Scalar("Id"));
        Assert.Equal("[Event] Box", first.Scalar("Name"));
        Assert.Equal("true", ((YamlMap)first.Get("Jobs")!).Scalar("Novice"));
        Assert.Equal("bonus bStr,1; // c\nif (1) {\n  bonus bAgi,2;\n}", first.Scalar("Script"));
        var second = Assert.IsType<YamlMap>(body.Items[1]);
        Assert.Equal(2, Assert.IsType<YamlSequence>(second.Get("Tags")).Items.Count);
    }

    [Fact]
    public void Loader_MatchesTheRealPinnedLoaderForEveryItem()
    {
        using var stream = File.OpenRead(Path.Combine(Root(), "tests/WorldDataImporter.Tests/Reference/item-db-loaded.txt.gz"));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var reference = reader.ReadToEnd().TrimEnd('\n').Split('\n');

        var result = LoadPinned();
        var actual = result.Items.Select(item => (item.Item with
        {
            ScriptIndex = item.Script is null ? -1 : 0,
            EquipScriptIndex = item.EquipScript is null ? -1 : 0,
            UnEquipScriptIndex = item.UnEquipScript is null ? -1 : 0,
        }).ToCanonicalLine()).ToArray();

        Assert.Equal(reference.Length, actual.Length);
        var differences = reference.Zip(actual).Where(pair => pair.First != pair.Second).Take(5).Select(pair => $"reference: {pair.First}\n   actual: {pair.Second}").ToArray();
        Assert.True(differences.Length == 0, string.Join("\n", differences));
    }

    [Fact]
    public void Loader_CoversEveryPinnedItem()
    {
        var result = LoadPinned();
        // 29,356 pinned rows + the loader's own ITEMID_DUMMY (499).
        Assert.Equal(29357, result.Items.Count);
        Assert.Equal(result.Items.Count, result.Items.Select(item => item.Item.Id).Distinct().Count());
        Assert.Contains(result.Items, item => item.Item.Id == 1201 && item.Item.AegisName == "Knife");
    }

    // The group loader against the real loader's stored groups (random option groups are not resolved by the dump, hence the 0 column).
    [Fact]
    public void GroupLoader_MatchesTheRealPinnedLoaderForEveryEntry()
    {
        using var stream = File.OpenRead(Path.Combine(Root(), "tests/WorldDataImporter.Tests/Reference/item-groups-loaded.txt.gz"));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var reference = reader.ReadToEnd().TrimEnd('\n').Split('\n');

        var items = LoadPinned().Items.Select(item => item.Item).ToDictionary(item => item.AegisName.ToLowerInvariant());
        var groups = ItemGroupLoader.Load(relative => File.ReadAllText(Path.Combine(Root(), "legacy/rathena", relative)), Constants(), items);
        var actual = groups.OrderBy(g => g.GroupId).SelectMany(g => g.SubGroups.OrderBy(s => s.SubGroup).SelectMany(s => s.Entries.OrderBy(e => e.Index).Select(e =>
            $"{g.GroupId}|{s.SubGroup}|{s.Algorithm}|{s.TotalRate}|{e.Index}|{e.ItemId}|{e.Rate}|{e.Amount}|{e.DurationMinutes}|{e.Bound}|{(e.Announced ? 1 : 0)}{(e.UniqueId ? 1 : 0)}{(e.Stacked ? 1 : 0)}{(e.Named ? 1 : 0)}|{(e.RandomOptionGroup is null ? 0 : -1)}|{e.RefineMinimum}|{e.RefineMaximum}|{e.GradeMinimum}|{e.GradeMaximum}|0")))
            .ToArray();

        Assert.Equal(reference.Length, actual.Length);
        // Random option group ids are resolved by name in the pack; compare everything else.
        static string Strip(string line) { var f = line.Split('|'); f[11] = f[11] == "0" ? "0" : "r"; return string.Join('|', f); }
        var differences = reference.Zip(actual).Where(pair => Strip(pair.First) != Strip(pair.Second)).Take(5).Select(pair => $"reference: {pair.First}\n   actual: {pair.Second}").ToArray();
        Assert.True(differences.Length == 0, string.Join("\n", differences));
    }
}
