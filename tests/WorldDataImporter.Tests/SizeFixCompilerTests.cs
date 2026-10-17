using Athena.WorldCompiler.Generation;

namespace Athena.WorldCompiler.Tests;

// Pinned SizeFixDatabase::parseBodyNode (status.cpp:501-581) merge semantics and the db/size_fix.yml Footer imports.
public sealed class SizeFixCompilerTests
{
    private const string Base = """
        Header:
          Type: SIZE_FIX_DB
          Version: 1

        Body:
          - Weapon: Fist
          - Weapon: Dagger
            Medium: 75
            Large: 50
          - Weapon: 1hSword
            Small: 75

        Footer:
          Imports:
          - Path: db/pre-re/size_fix.yml
            Mode: Prerenewal
          - Path: db/re/size_fix.yml
            Mode: Renewal
          - Path: db/import/size_fix.yml
        """;

    private static string Entry(string weapon, params (string Size, int Value)[] sizes) =>
        $"Header:\n  Type: SIZE_FIX_DB\n  Version: 1\n\nBody:\n  - Weapon: {weapon}\n" + string.Concat(sizes.Select(s => $"    {s.Size}: {s.Value}\n"));

    // db/re/size_fix.yml is a required Renewal import in the pinned Footer, so it defaults to a no-op row here.
    private static int[,] Build(Dictionary<string, string> files, string baseYaml = Base) =>
        SizeFixCompiler.BuildTable(path => path == "db/size_fix.yml" ? baseYaml : files.GetValueOrDefault(path) ?? (path == "db/re/size_fix.yml" ? Entry("Fist") : null));

    private static string AllWeapons() => Base.Replace("  - Weapon: 1hSword\n    Small: 75\n", string.Concat(new[] { "1hSword", "2hSword", "1hSpear", "2hSpear", "1hAxe", "2hAxe", "Mace", "2hMace", "Staff", "Bow", "Knuckle", "Musical", "Whip", "Book", "Katar", "Revolver", "Rifle", "Gatling", "Shotgun", "Grenade", "Huuma", "2hStaff" }.Select(w => $"  - Weapon: {w}\n")));

    [Fact]
    public void BaseTable_AppliesEntriesAndDefaultsUnnamedSizesTo100()
    {
        var table = Build([], AllWeapons());
        Assert.Equal((100, 75, 50), (table[1, 0], table[1, 1], table[1, 2]));
        Assert.Equal((100, 100, 100), (table[0, 0], table[0, 1], table[0, 2]));
    }

    [Fact]
    public void RenewalImport_OverridesOnlyNamedSizes_AndPrerenewalImportIsSkipped()
    {
        var table = Build(new()
        {
            ["db/re/size_fix.yml"] = Entry("Dagger", ("Large", 25)),
            ["db/pre-re/size_fix.yml"] = Entry("Dagger", ("Small", 10)),
        }, AllWeapons());
        Assert.Equal((100, 75, 25), (table[1, 0], table[1, 1], table[1, 2])); // Medium kept, Large replaced, pre-re ignored.
    }

    [Fact]
    public void OperatorImport_AppliesLast_AndIsOptional()
    {
        var table = Build(new()
        {
            ["db/re/size_fix.yml"] = Entry("Dagger", ("Large", 25)),
            ["db/import/size_fix.yml"] = Entry("Dagger", ("Large", 90), ("Small", 80)),
        }, AllWeapons());
        Assert.Equal((80, 75, 90), (table[1, 0], table[1, 1], table[1, 2]));
        Assert.Equal(75, Build([], AllWeapons())[1, 1]); // no import files at all: still builds
    }

    [Fact]
    public void ValueAbove100_IsClampedLikeThePinnedLoader()
    {
        var table = Build(new() { ["db/import/size_fix.yml"] = Entry("Dagger", ("Small", 150)) }, AllWeapons());
        Assert.Equal(100, table[1, 0]);
    }

    [Fact]
    public void UnknownWeapon_AndMissingRequiredImport_FailLoudly()
    {
        Assert.Throws<ArgumentException>(() => Build(new() { ["db/re/size_fix.yml"] = Entry("Lance", ("Small", 50)) }, AllWeapons()));
        Assert.Throws<ArgumentException>(() => SizeFixCompiler.BuildTable(path => path == "db/size_fix.yml" ? AllWeapons() : null)); // db/re import absent
    }

    [Fact]
    public void WeaponWithNoEntry_FailsRatherThanDefaulting()
    {
        Assert.Throws<ArgumentException>(() => Build(new() { ["db/re/size_fix.yml"] = Entry("Dagger") }, Base));
    }
}
