using Athena.Net.MapServer.World.Items;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// Custom item layers (data/custom/items/*.yml) run through the SAME ItemDatabaseLoader.ParseNode as the pinned databases:
// an existing Id is re-parsed onto the pinned draft (omitted keys inherit), a new Id is a full definition.
public sealed class CustomItemLayerTests
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

    private static readonly Lazy<ItemDatabaseLoadResult> Pinned = new(() => ItemDatabaseLoader.Load(Read, Constants()));

    private static ItemDatabaseLoadResult LoadWith(params string[] customYaml) =>
        ItemDatabaseLoader.Load(Read, Constants(), customLayers: customYaml.Select((yaml, index) => ($"custom/items/{index}.yml", "Body:\n" + yaml)).ToArray());

    private static LoadedItem Item(ItemDatabaseLoadResult result, uint id) => result.Items.Single(item => item.Item.Id == id);

    [Fact]
    public void ExistingId_OverridesOnlyTheGivenFields_AndInheritsEveryOtherField()
    {
        var result = LoadWith("""
              - Id: 501
                Buy: 1000
                Weight: 5
            """);
        var original = Item(Pinned.Value, 501);
        var overridden = Item(result, 501);

        Assert.Equal(1000, overridden.Item.Buy);
        Assert.Equal(5u, overridden.Item.Weight);
        // Sell was derived from Buy (pinned has no Sell); Finish re-derives it from the merged Buy.
        Assert.Equal(500, overridden.Item.Sell);
        Assert.Equal(original.Item with { Buy = 1000, Sell = 500, Weight = 5 }, overridden.Item);
        Assert.Equal(original.Script, overridden.Script);
        Assert.Equal(Pinned.Value.Items.Count, result.Items.Count);
    }

    [Fact]
    public void ScriptKey_ReplacesTheWholeScript_AndAnAbsentScriptKeepsTheOriginal()
    {
        var replaced = Item(LoadWith("""
              - Id: 501
                Script: |
                  percentheal 5,0;
            """), 501);
        Assert.Equal("percentheal 5,0;", replaced.Script);

        var kept = Item(LoadWith("""
              - Id: 501
                Name: Renamed Red Potion
            """), 501);
        Assert.Equal("Renamed Red Potion", kept.Item.Name);
        Assert.Equal(Item(Pinned.Value, 501).Script, kept.Script);
    }

    [Fact]
    public void PresentMapField_ReplacesWholesale_AbsentMapFieldIsInherited()
    {
        Assert.True(Item(Pinned.Value, 909).Item.Flags.HasFlag(PackItemFlags.BuyingStore));

        var replaced = Item(LoadWith("""
              - Id: 909
                Flags:
                  DropAnnounce: true
            """), 909);
        Assert.False(replaced.Item.Flags.HasFlag(PackItemFlags.BuyingStore));
        Assert.True(replaced.Item.Flags.HasFlag(PackItemFlags.DropAnnounce));

        var inherited = Item(LoadWith("""
              - Id: 909
                Trade:
                  NoDrop: true
            """), 909);
        Assert.True(inherited.Item.Flags.HasFlag(PackItemFlags.BuyingStore));
        Assert.True(inherited.Item.Flags.HasFlag(PackItemFlags.NoDrop));
    }

    [Fact]
    public void NewId_AddsAFullDefinition_ThroughTheSameParser()
    {
        var result = LoadWith("""
              - Id: 900100
                AegisName: Custom_Test_Blade
                Name: Custom Test Blade
                Type: Weapon
                SubType: Dagger
                Buy: 20
                Attack: 30
                Locations:
                  Right_Hand: true
                Script: |
                  bonus bStr,1;
            """);
        var item = Item(result, 900100);

        Assert.Equal(PackItemTypes.Weapon, item.Item.Type);
        Assert.Equal(30u, item.Item.Attack);
        Assert.Equal(10, item.Item.Sell);
        Assert.Equal("bonus bStr,1;", item.Script);
        // Finish ran exactly once on the final draft: the missing weapon level is defaulted (and warned) once.
        Assert.Equal(1u, item.Item.WeaponLevel);
        Assert.Single(result.Warnings, warning => warning == "item 900100: weapon without weapon level, defaulting to 1");
        Assert.Equal(Pinned.Value.Items.Count + 1, result.Items.Count);
    }

    [Theory]
    [InlineData("AegisName")]
    [InlineData("Name")]
    public void NewId_MissingRequiredField_FailsWithFileLineIdAndField(string missing)
    {
        var fields = new Dictionary<string, string> { ["AegisName"] = "Custom_Missing", ["Name"] = "Custom Missing" };
        fields.Remove(missing);
        var yaml = "  - Id: 900101\n" + string.Concat(fields.Select(field => $"    {field.Key}: {field.Value}\n"));

        var error = Assert.Throws<InvalidDataException>(() => LoadWith(yaml));
        Assert.Equal($"custom/items/0.yml:2: item 900101 has no {missing}.", error.Message);
    }

    [Fact]
    public void DuplicateIdAcrossCustomLayers_FailsWithBothLocations()
    {
        var error = Assert.Throws<InvalidDataException>(() => LoadWith("  - Id: 501\n    Buy: 1\n", "  - Id: 501\n    Buy: 2\n"));
        Assert.Equal("custom/items/1.yml:2: item id 501 is defined twice in the custom layers (first at custom/items/0.yml:2).", error.Message);
    }

    [Theory]
    [InlineData("  - Id: 501\n    Wieght: 5\n", "custom/items/0.yml:3: item 501 has unknown field 'Wieght'.")]
    [InlineData("  - Id: 501\n    Trade:\n      NoDorp: true\n", "custom/items/0.yml:4: item 501 has unknown field 'Trade.NoDorp'.")]
    public void UnknownField_FailsLoudly(string yaml, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => LoadWith(yaml));
        Assert.Equal(message, error.Message);
    }

    // Every pinned item re-applied as an override of itself: the custom schema accepts every key the pinned data uses, and
    // the exists=true parse path reproduces all 29k pinned items exactly (only the new-item path is checked against the
    // real rAthena loader dump in ItemDatabaseLoaderTests).
    [Fact]
    public void ReapplyingTheWholePinnedDatabaseAsACustomLayer_IsTheIdentity()
    {
        var reapplied = ItemDatabaseLoader.Load(Read, Constants(), customLayers: ItemDatabaseLoader.ImportOrder.Select(path => (path, Read(path))).ToArray());

        Assert.Equal(Pinned.Value.Items, reapplied.Items);
    }

    [Fact]
    public void FullPackRegeneration_WithCustomLayer_IsByteIdentical()
    {
        IReadOnlyList<(string, string)> custom = [("custom/items/a.yml", "Body:\n  - Id: 501\n    Buy: 99\n    Script: |\n      percentheal 1,0;\n  - Id: 900102\n    AegisName: Custom_Det\n    Name: Custom Det\n")];
        var first = ItemPackFormat.Write(ItemPackBuilder.Build(Read, Constants(), Commit, custom).Pack);
        var second = ItemPackFormat.Write(ItemPackBuilder.Build(Read, Constants(), Commit, custom).Pack);

        Assert.Equal(first, second);
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(Root(), "src/MapServer/Generated/Assets/Items/AthenaItems.bin")), first);
    }
}
