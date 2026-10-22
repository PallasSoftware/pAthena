using Athena.Net.MapServer.World.Items;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// Proves the data-driven-engine mandate end to end at the converter layer: a brand-new, never-before-seen item id,
// defined ONLY in rAthena-shaped source data (never referenced by any identity check in product code), flows through
// the REAL WorldDataImporter pipeline (ItemDatabaseLoader -> ItemNoEquipLoader -> ItemPackBuilder -> ItemPackFormat
// binary round-trip) and comes out carrying every mechanic this increment (and the earlier container/cooldown slices)
// already supports generically - with ZERO item-specific production code added for this fixture id.
//
// This test deliberately stops at the PackItem/PackScript layer (not ItemCatalog/ItemDefinition): WorldDataImporter.Tests
// cannot reference MapServer.csproj directly - both projects compile the SAME source file
// (src/MapServer/World/Items/ItemPackFormat.cs, linked into WorldDataImporter via <Compile Include>) into two separate
// assemblies, so referencing both from one test project produces CS0433 "type exists in both assemblies" ambiguity for
// every PackItem/PackScript/IrNode usage (reproduced while writing this test). The catalog-interpretation layer
// (ItemCatalog.Build's container-Grants detection, Meta construction) is generic code with no per-item branching either -
// see ItemCatalogTests.Catalog_ContainersKeepTheirConstantGetItemGrants in MapServer.Tests, which already proves that
// exact logic against real pinned data; it does not need a second fixture-id proof since the code path is identical
// regardless of which item id reaches it.
public sealed class ItemPipelineDataDrivenFixtureTests
{
    // Clearly outside the real pinned id space (29,356 rows top out far below this) so it can never collide with a real item.
    private const uint FixtureItemId = 900_001;
    private const uint HealFixtureItemId = 900_004;
    private const uint UnsupportedFixtureItemId = 900_005;
    private const uint GrantItemId = 501; // Red_Potion - a real pinned item, so a grant-resolution check downstream (as
                                           // MapClientSession.HandleIroUseItemRequestAsync performs) could resolve it.

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    // A minimal synthetic rAthena root: only item_db_usable.yml/item_noequip.txt carry the fixture; every other file the
    // real ItemPackBuilder.Build needs (item_db_equip.yml, item_db_etc.yml, item_group_db.yml, script.cpp for builtin
    // names) is either an empty-but-valid stub or the REAL pinned file, never hand-rolled parsing logic duplicated here.
    private static string BuildSyntheticRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "athena-item-pipeline-fixture-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "db", "re"));
        Directory.CreateDirectory(Path.Combine(root, "src", "map"));

        File.WriteAllText(Path.Combine(root, "db", "re", "item_db_usable.yml"), $"""
            Header:
              Type: ITEM_DB
              Version: 1
            Body:
              - Id: {FixtureItemId}
                AegisName: Test_Fixture_Restricted_Container
                Name: Test Fixture Restricted Container
                Type: Usable
                Buy: 10
                Sell: 5
                Weight: 10
                Delay:
                  Duration: 1000
                  Status: Reuse_Limit_A
                Script: |
                  getitem {GrantItemId},5;
              - Id: {HealFixtureItemId}
                AegisName: Test_Fixture_Healing_Draught
                Name: Test Fixture Healing Draught
                Type: Healing
                Script: |
                  itemheal rand(10,20),5;
                  percentheal 3,0;
                  sc_start SC_BLESSING,1000,2;
              - Id: {UnsupportedFixtureItemId}
                AegisName: Test_Fixture_Unsupported_Scroll
                Name: Test Fixture Unsupported Scroll
                Type: Usable
                Script: |
                  itemheal 10,0;
                  warp "prontera",150,150;
            """);
        // The block-only YAML subset this importer parses has no "Body: []" empty-sequence form, so each otherwise-empty
        // database gets one harmless placeholder Etc item (outside the fixture's own id and every real pinned id range).
        File.WriteAllText(Path.Combine(root, "db", "re", "item_db_equip.yml"), """
            Header:
              Type: ITEM_DB
              Version: 1
            Body:
              - Id: 900002
                AegisName: Test_Fixture_Placeholder_Equip
                Name: Test Fixture Placeholder Equip
            """);
        File.WriteAllText(Path.Combine(root, "db", "re", "item_db_etc.yml"), """
            Header:
              Type: ITEM_DB
              Version: 1
            Body:
              - Id: 900003
                AegisName: Test_Fixture_Placeholder_Etc
                Name: Test Fixture Placeholder Etc
            """);
        File.WriteAllText(Path.Combine(root, "db", "re", "item_group_db.yml"), """
            Header:
              Type: ITEM_GROUP_DB
              Version: 1
            Body:
              - Group: 10LVUP
                SubGroups:
                  - SubGroup: 0
                    List:
                      - Index: 0
                        Item: Test_Fixture_Placeholder_Etc
                        Rate: 1
            """);
        // The real db/re/item_noequip.txt format (see ItemNoEquipLoader): "<item_id>,<bitmask>" - bit 1 "restricted in
        // normal maps", exactly the real pinned format this increment parses for real rows like 2483 (Siege_Greave).
        File.WriteAllText(Path.Combine(root, "db", "re", "item_noequip.txt"), $"{FixtureItemId},1 // Test_Fixture_Restricted_Container\n");
        // RandomOptionLoader.Load needs this file to exist too (see item_group_db.yml's own placeholder-row comment
        // above - the block-only YAML subset has no "Body: []" empty-sequence form).
        File.WriteAllText(Path.Combine(root, "db", "re", "item_randomopt_db.yml"), """
            Header:
              Type: RANDOM_OPTION_DB
              Version: 1
            Body:
              - Id: 1
                Option: VAR_MAXHPAMOUNT
                Script: |
                  bonus bMaxHP,getrandomoptinfo(ROA_VALUE);
            """);
        // RathenaBuiltins.Names only needs real script builtin names; reuse the real pinned file rather than faking one.
        File.Copy(Path.Combine(Root(), "legacy/rathena/src/map/script.cpp"), Path.Combine(root, "src", "map", "script.cpp"));

        return root;
    }

    private static RathenaConstantTable Constants() => RathenaConstantTable.Load(File.ReadAllText(Path.Combine(Root(), "tools/WorldDataImporter/rathena-constants.tsv")));

    [Fact]
    public void FixtureItem_FlowsThroughTheRealConverterWithNoItemSpecificProductionCode()
    {
        var syntheticRoot = BuildSyntheticRoot();
        try
        {
            string ReadFile(string relative) => File.ReadAllText(Path.Combine(syntheticRoot, relative));

            // The REAL converter entry point - the same ItemPackBuilder.Build the WorldDataImporter CLI's
            // compile-item-pack command calls - not a hand-assembled PackItem.
            var built = ItemPackBuilder.Build(ReadFile, Constants(), new string('a', 40));

            // Round-trip through the REAL binary pack format (write, then read back), exactly like a real
            // "run WorldDataImporter -> get a data pack -> start pAthena" cycle.
            var reloaded = ItemPackFormat.Read(ItemPackFormat.Write(built.Pack));

            var item = Assert.Single(reloaded.Items, candidate => candidate.Id == FixtureItemId);

            // Mechanic 1 (already-supported, generic): the getitem-only script is lowered into the typed IR exactly the
            // same way any real container item's script is - this is what ItemCatalog.Build's Grants() detection reads.
            var script = reloaded.Scripts[item.ScriptIndex];
            Assert.Equal(PackScriptSupport.Lowered, script.Support);
            Assert.Equal(IrKind.Block, script.Program!.Kind);
            var statement = Assert.Single(script.Program.Items!);
            Assert.Equal("getitem", statement.Text);
            Assert.Equal((long)GrantItemId, statement.Items![0].Number);
            Assert.Equal(5L, statement.Items[1].Number);

            // Mechanic 2 (already-supported, generic): Delay-group cooldown fields (5609d36).
            Assert.Equal(1000u, item.DelayDuration);
            Assert.NotEqual(-1, item.DelayStatus); // SC_NONE would be -1; Reuse_Limit_A resolved to a real SC_ constant.

            // Mechanic 3 (this increment): the no_equip bitmask from item_noequip.txt, loaded generically by id, not identity.
            Assert.Equal(1u, item.NoEquip);
        }
        finally
        {
            Directory.Delete(syntheticRoot, recursive: true);
        }
    }

    // The binding data-driven acceptance requirement for item-script EXECUTION: a never-before-seen Healing item declaring
    // itemheal/percentheal/sc_start, converted by the real importer and round-tripped through the real pack format, plans to the
    // exact effects the MapServer evaluator (the same source file, linked into this project) dispatches - with no code naming it.
    // A second unseen item using a command the evaluator does not run gets an actionable diagnostic instead of a partial effect.
    [Fact]
    public void FixtureScripts_ExecuteThroughTheGenericEvaluator_OrNameWhatBlocksThem()
    {
        var syntheticRoot = BuildSyntheticRoot();
        try
        {
            string ReadFile(string relative) => File.ReadAllText(Path.Combine(syntheticRoot, relative));
            var reloaded = ItemPackFormat.Read(ItemPackFormat.Write(ItemPackBuilder.Build(ReadFile, Constants(), new string('a', 40)).Pack));
            PackScript ScriptOf(uint id) => reloaded.Scripts[reloaded.Items.Single(item => item.Id == id).ScriptIndex];

            var rolls = new List<(long, long)>();
            var plan = ItemScriptEvaluator.Plan(ScriptOf(HealFixtureItemId), (min, max) => { rolls.Add((min, max)); return 17; });
            Assert.True(plan.Executable, plan.Unsupported);
            Assert.Equal([(10L, 20L)], rolls);
            Assert.Equal(
                [("itemheal", new long[] { 17, 5 }), ("percentheal", [3, 0]), ("sc_start", [30, 1000, 2])],
                plan.Effects.Select(effect => (effect.Command, effect.Arguments.ToArray())).ToArray());

            var unsupported = ItemScriptEvaluator.Plan(ScriptOf(UnsupportedFixtureItemId), (min, _) => min);
            Assert.False(unsupported.Executable);
            Assert.Equal("command:warp owner:OtherSystem", unsupported.Unsupported);
            Assert.Empty(unsupported.Effects);
        }
        finally
        {
            Directory.Delete(syntheticRoot, recursive: true);
        }
    }
}
