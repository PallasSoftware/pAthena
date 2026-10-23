using System.Text.Json;
using System.Text.Json.Serialization;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// Custom mob layers (data/custom/mobs/*.yml) run through the SAME MobDataCompiler.ParseMobNode as the pinned mob_db.yml:
// an existing Id inherits every omitted key from its pinned block, a new Id is a full definition.
public sealed class CustomMobLayerTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly Lazy<string> PinnedYaml = new(() => File.ReadAllText(Path.Combine(Root(), "legacy/rathena/db/re/mob_db.yml")));
    private static readonly Lazy<IReadOnlyList<MobDataCompiler.MobDefinitionData>> Pinned = new(() => MobDataCompiler.ReadAllMobDefinitions(PinnedYaml.Value));

    private static IReadOnlyList<MobDataCompiler.MobDefinitionData> LoadWith(string pinnedYaml, params string[] customYaml) =>
        MobDataCompiler.ReadAllMobDefinitions(pinnedYaml, customYaml.Select((yaml, index) => ($"custom/mobs/{index}.yml", "Body:\n" + yaml)).ToArray());

    private static string Serialize(MobDataCompiler.MobDefinitionData mob) => JsonSerializer.Serialize(mob, Json);

    private const string SmallPinned = "Body:\n  - Id: 1\n    AegisName: ALPHA\n    Name: Alpha\n    AttackMotion: 500\n    Ai: 01\n    Modes:\n      Detector: true\n    Drops:\n      - Item: Apple\n        Rate: 100\n      - Item: Jellopy\n        Rate: 200\n";

    [Fact]
    public void ExistingId_OverridesOnlyTheGivenFields_AndInheritsEveryOtherField()
    {
        var mobs = LoadWith(PinnedYaml.Value, "  - Id: 1002\n    Hp: 9999\n    Level: 50\n");
        var original = Pinned.Value.Single(mob => mob.Id == 1002);
        var overridden = mobs.Single(mob => mob.Id == 1002);

        Assert.Equal(9999u, overridden.Hp);
        Assert.Equal(50, overridden.Level);
        Assert.Equal(("custom/mobs/0.yml", 2), (overridden.SourceFile, overridden.SourceLine));
        Assert.Equal(Serialize(original with { Hp = 9999, Level = 50, SourceFile = "custom/mobs/0.yml", SourceLine = 2 }), Serialize(overridden));
        Assert.Equal(Pinned.Value.Count, mobs.Count);
    }

    [Fact]
    public void OmittedDerivedFields_KeepThePinnedResolvedValues()
    {
        var mob = Assert.Single(LoadWith(SmallPinned, "  - Id: 1\n    Name: Beta\n    AttackMotion: 700\n"));

        Assert.Equal("Beta", mob.Name);
        Assert.Equal(700, mob.AttackMotion);
        Assert.Equal("Alpha", mob.JapaneseName);    // pinned jname fell back to the pinned Name
        Assert.Equal(500, mob.ClientAttackMotion);  // pinned value fell back to the pinned AttackMotion
    }

    [Fact]
    public void PresentListFields_ReplaceWholesale_AndAiAndModesAreEachInheritedWhenOmitted()
    {
        var replaced = Assert.Single(LoadWith(SmallPinned, "  - Id: 1\n    Modes:\n      Aggressive: true\n    Drops:\n      - Item: Red_Potion\n        Rate: 50\n"));
        Assert.Equal(MobDataCompiler.MobModeData.CanMove | MobDataCompiler.MobModeData.CanAttack | MobDataCompiler.MobModeData.Aggressive, replaced.Mode);
        Assert.Equal([new MobDataCompiler.MobDropEntryData("Red_Potion", 50, false, null)], replaced.Drops);

        var newAi = Assert.Single(LoadWith(SmallPinned, "  - Id: 1\n    Ai: 02\n"));
        Assert.Equal(MobDataCompiler.MobModeData.CanMove | MobDataCompiler.MobModeData.Looter | MobDataCompiler.MobModeData.CanAttack | MobDataCompiler.MobModeData.Detector, newAi.Mode);
        Assert.Equal(["Apple", "Jellopy"], newAi.Drops.Select(drop => drop.Item));
    }

    [Fact]
    public void NewId_AddsAFullDefinition_WithItsOwnProvenance()
    {
        var mobs = LoadWith(PinnedYaml.Value, "  - Id: 900200\n    AegisName: CUSTOM_PORING\n    Name: Custom Poring\n    Level: 3\n    Hp: 70\n");
        var mob = mobs.Single(candidate => candidate.Id == 900200);

        Assert.Equal(("CUSTOM_PORING", "Custom Poring", "Custom Poring", 3, 70u, 1, 150), (mob.AegisName, mob.Name, mob.JapaneseName, mob.Level, mob.Hp, mob.Str, mob.WalkSpeed));
        Assert.Equal(Pinned.Value.Count + 1, mobs.Count);

        var generated = MobDataCompiler.GenerateMobDefinitions([(mob, "CustomPoring")], "pin", "GeneratedMobs", "legacy/rathena/db/re/mob_db.yml", 0);
        Assert.Contains("Source: new WorldSourceInfo(\"Athena.NET\", \"\", \"custom/mobs/0.yml\", 2)", generated);
    }

    [Theory]
    [InlineData("AegisName")]
    [InlineData("Name")]
    public void NewId_MissingRequiredField_FailsWithFileLineIdAndField(string missing)
    {
        var fields = new Dictionary<string, string> { ["AegisName"] = "CUSTOM_MISSING", ["Name"] = "Custom Missing" };
        fields.Remove(missing);
        var yaml = "  - Id: 900201\n" + string.Concat(fields.Select(field => $"    {field.Key}: {field.Value}\n"));

        var error = Assert.Throws<ArgumentException>(() => LoadWith(SmallPinned, yaml));
        Assert.Equal($"custom/mobs/0.yml:2: mob 900201 has no '{missing}' field.", error.Message);
    }

    [Fact]
    public void DuplicateIdAcrossCustomLayers_FailsWithBothLocations()
    {
        var error = Assert.Throws<InvalidDataException>(() => LoadWith(SmallPinned, "  - Id: 1\n    Hp: 1\n", "  - Id: 1\n    Hp: 2\n"));
        Assert.Equal("custom/mobs/1.yml:2: mob id 1 is defined twice in the custom layers (first at custom/mobs/0.yml:2).", error.Message);
    }

    [Theory]
    [InlineData("  - Id: 1\n    Hpp: 5\n", "custom/mobs/0.yml:3: mob 1 has unknown field 'Hpp'.")]
    [InlineData("  - Id: 1\n    Modes:\n      Agressive: true\n", "custom/mobs/0.yml:4: mob 1 has unknown field 'Modes.Agressive'.")]
    [InlineData("  - Id: 1\n    Drops:\n      - Item: Apple\n        Rat: 5\n", "custom/mobs/0.yml:5: mob 1 has unknown field 'Drops.Rat'.")]
    public void UnknownField_FailsLoudly(string yaml, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => LoadWith(SmallPinned, yaml));
        Assert.Equal(message, error.Message);
    }

    // Every pinned mob re-applied as an override of itself reproduces the pinned dataset (apart from provenance), and the
    // custom schema accepts every key the pinned mob_db.yml uses.
    [Fact]
    public void ReapplyingThePinnedMobDbAsACustomLayer_IsTheIdentity()
    {
        var reapplied = MobDataCompiler.ReadAllMobDefinitions(PinnedYaml.Value, [("legacy/rathena/db/re/mob_db.yml", PinnedYaml.Value)]);

        Assert.Equal(
            Pinned.Value.Select(Serialize),
            reapplied.Select(mob => Serialize(mob with { SourceFile = null })));
    }

    [Fact]
    public void ItemAndMobIdsAreIndependentNamespaces()
    {
        // 501 is a real pinned item (Red_Potion) and 1002 a real pinned mob (Poring); each id means something different
        // in the other database, and a custom definition in one never touches the other.
        var mobs = LoadWith(PinnedYaml.Value, "  - Id: 501\n    AegisName: CUSTOM_MOB_501\n    Name: Custom Mob 501\n");
        Assert.Equal("Custom Mob 501", mobs.Single(mob => mob.Id == 501).Name);

        string Read(string relative) => File.ReadAllText(Path.Combine(Root(), "legacy/rathena", relative));
        var constants = RathenaConstantTable.Load(File.ReadAllText(Path.Combine(Root(), "tools/WorldDataImporter/rathena-constants.tsv")));
        var items = ItemDatabaseLoader.Load(Read, constants, customLayers: [("custom/items/0.yml", "Body:\n  - Id: 1002\n    AegisName: Custom_Item_1002\n    Name: Custom Item 1002\n")]);
        Assert.Equal("Red Potion", items.Items.Single(item => item.Item.Id == 501).Item.Name);
        Assert.Equal("Custom Item 1002", items.Items.Single(item => item.Item.Id == 1002).Item.Name);
        Assert.Equal("Poring", mobs.Single(mob => mob.Id == 1002).Name);
    }

    [Fact]
    public void FullMobGeneration_WithCustomLayer_IsByteDeterministic()
    {
        string Generate()
        {
            var mobs = LoadWith(PinnedYaml.Value, "  - Id: 1002\n    Hp: 77\n", "  - Id: 900202\n    AegisName: CUSTOM_DET\n    Name: Custom Det\n");
            var symbols = MobDataCompiler.CreateGeneratedSymbols(mobs);
            return MobDataCompiler.GenerateMobRegistry(symbols, "pin", "legacy/rathena/db/re/mob_db.yml") + string.Concat(
                symbols.GroupBy(item => item.Mob.Id / 1000).OrderBy(group => group.Key)
                    .Select(group => MobDataCompiler.GenerateMobDefinitions(group.Select(item => (item.Mob, item.Symbol)).ToArray(), "pin", "GeneratedMobs", "legacy/rathena/db/re/mob_db.yml", 0)));
        }

        var first = Generate();
        Assert.Equal(first, Generate());
        Assert.Contains("CustomDet = new(Id: 900202", first);
    }
}
