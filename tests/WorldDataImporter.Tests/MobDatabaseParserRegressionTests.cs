using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// Complete-dataset regression oracle for MobDataCompiler's mob_db.yml parsing: every field of every pinned
// MobDefinitionData (nested RaceGroups/Drops/MvpDrops/Mode and SourceLine included), one JSON line per mob in file order.
// The golden file was captured from the pre-YamlMap regex/substring scanner, so it proves the YamlMap port is
// behavior-identical for the whole pinned db/re/mob_db.yml, not a sample. A legitimate change (pinned rAthena bump,
// deliberate parsing fix) is regenerated with ATHENA_UPDATE_MOB_GOLDEN=1 and reviewed as a data diff.
public sealed class MobDatabaseParserRegressionTests
{
    private const string GoldenPath = "tests/WorldDataImporter.Tests/Reference/mob-db-parsed.jsonl.gz";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void CompletePinnedMobDb_ParsesIdenticallyToGolden_ThroughBothEntryPoints()
    {
        var yaml = File.ReadAllText(Path.Combine(Root(), "legacy/rathena/db/re/mob_db.yml"));
        var all = MobDataCompiler.ReadAllMobDefinitions(yaml);
        var actual = all.Select(mob => JsonSerializer.Serialize(mob, Json)).ToArray();
        var golden = Path.Combine(Root(), GoldenPath);

        if (Environment.GetEnvironmentVariable("ATHENA_UPDATE_MOB_GOLDEN") == "1")
        {
            using var output = new GZipStream(File.Create(golden), CompressionLevel.SmallestSize);
            using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
            foreach (var line in actual) writer.WriteLine(line);
        }

        using var stream = File.OpenRead(golden);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var expected = reader.ReadToEnd().TrimEnd('\n').Split('\n');

        Assert.Equal(2675, expected.Length);
        Assert.Equal(expected.Length, actual.Length);
        AssertSame(expected, actual, "ReadAllMobDefinitions");

        // The single-mob lookup path must resolve every pinned id to the same definition as the full scan.
        var single = all.Select(mob => JsonSerializer.Serialize(MobDataCompiler.ReadMobDefinition(yaml, mob.Id), Json)).ToArray();
        AssertSame(expected, single, "ReadMobDefinition");
    }

    private static void AssertSame(string[] expected, string[] actual, string path)
    {
        var mismatches = Enumerable.Range(0, expected.Length).Where(i => expected[i] != actual[i]).ToArray();
        Assert.True(mismatches.Length == 0,
            $"{path}: {mismatches.Length}/{expected.Length} mobs differ from the golden parse. First:\n" +
            string.Join("\n", mismatches.Take(3).Select(i => $"expected {expected[i]}\nactual   {actual[i]}")));
    }
}
