using System.Text.RegularExpressions;
using Athena.WorldCompiler.Generation;

public sealed class RefineDataCompilerTests
{
    // The fixture YAML carries no Chances block at all, so this resolver is never actually invoked by the
    // fixture-based tests below - it exists only to satisfy BuildTable's required parameter.
    private static int NoMaterialsExpected(string aegisName) => throw new InvalidOperationException($"Unexpected material resolution for '{aegisName}'.");

    // Mirrors Program.cs's own BuildAegisNameToItemIdResolver (same regex shape QuestDropDataCompiler already
    // uses per-lookup) against the REAL pinned item_db_*.yml files, for tests that exercise real refine.yml
    // Material names.
    private static Func<string, int> RealMaterialResolver(string root)
    {
        var byAegis = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in new[] { "legacy/rathena/db/re/item_db_equip.yml", "legacy/rathena/db/re/item_db_etc.yml", "legacy/rathena/db/re/item_db_usable.yml" })
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            foreach (Match match in Regex.Matches(text, @"^  - Id: (\d+)\n    AegisName: (\S+)$", RegexOptions.Multiline))
                byAegis[match.Groups[2].Value] = int.Parse(match.Groups[1].Value);
        }
        return aegisName => byAegis.TryGetValue(aegisName, out var id) ? id : throw new ArgumentException($"Material AegisName '{aegisName}' was not found.");
    }

    private const string Fixture = """
        Header:
          Type: REFINE_DB
          Version: 2

        Body:
          - Group: Weapon
            Levels:
              - Level: 1
                RefineLevels:
                  - Level: 1
                    Bonus: 200
                  - Level: 2
                    Bonus: 400
                    RandomBonus: 300
              - Level: 2
                RefineLevels:
                  - Level: 1
                    Bonus: 100
          - Group: Armor
            Levels:
              - Level: 1
                RefineLevels:
                  - Level: 1
                    Bonus: 100
          - Group: Shadow_Weapon
            Levels:
              - Level: 1
                RefineLevels:
                  - Level: 1
                    Bonus: 50
        """;

    private const string FixtureWithChances = """
        Header:
          Type: REFINE_DB
          Version: 2

        Body:
          - Group: Weapon
            Levels:
              - Level: 1
                RefineLevels:
                  - Level: 1
                    Bonus: 200
                    Chances:
                      - Type: Normal
                        Rate: 10000
                        Price: 50
                        Material: TestOre
                      - Type: HD
                        Rate: 9000
                        Price: 20000
                        Material: TestHdOre
                        BreakingRate: 500
                        DowngradeAmount: 1
                  - Level: 2
                    Bonus: 400
          - Group: Armor
            Levels:
              - Level: 1
                RefineLevels:
                  - Level: 1
                    Bonus: 100
        """;

    private static int FixtureMaterialResolver(string aegisName) => aegisName switch
    {
        "TestOre" => 9001,
        "TestHdOre" => 9002,
        _ => throw new ArgumentException($"Unexpected material '{aegisName}' in FixtureWithChances."),
    };

    [Fact]
    public void BuildTable_ParsesWeaponAndArmorBonusValues()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture, NoMaterialsExpected);
        Assert.Equal(200u, table["Weapon"][1][1].Bonus);
        Assert.Equal(400u, table["Weapon"][1][2].Bonus);
        Assert.Equal(100u, table["Weapon"][2][1].Bonus);
        Assert.Equal(100u, table["Armor"][1][1].Bonus);
    }

    [Fact]
    public void BuildTable_ParsesRandomBonusMaxValue_DefaultsToZeroWhenAbsent()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture, NoMaterialsExpected);
        Assert.Equal(0u, table["Weapon"][1][1].RandomBonusMax);
        Assert.Equal(300u, table["Weapon"][1][2].RandomBonusMax);
    }

    [Fact]
    public void BuildTable_ParsesChancesIntoCostsKeyedByType()
    {
        var table = RefineDataCompiler.BuildTable(_ => FixtureWithChances, FixtureMaterialResolver);

        var costs = table["Weapon"][1][1].Costs!;
        Assert.Equal((10000u, 50u, 9001, 0u, 0u), (costs["Normal"].Rate, costs["Normal"].Price, costs["Normal"].MaterialItemId, costs["Normal"].BreakingRate, costs["Normal"].DowngradeAmount));
        Assert.Equal((9000u, 20000u, 9002, 500u, 1u), (costs["HD"].Rate, costs["HD"].Price, costs["HD"].MaterialItemId, costs["HD"].BreakingRate, costs["HD"].DowngradeAmount));
    }

    [Fact]
    public void BuildTable_Costs_IsNullWhenNoChancesBlockExists()
    {
        var table = RefineDataCompiler.BuildTable(_ => FixtureWithChances, FixtureMaterialResolver);
        Assert.Null(table["Weapon"][1][2].Costs); // refine 2 has Bonus but no Chances in the fixture.
        Assert.Null(table["Armor"][1][1].Costs);
    }

    [Fact]
    public void BuildTable_UnknownChanceType_Throws()
    {
        const string badType = """
            Header:
              Type: REFINE_DB
              Version: 2
            Body:
              - Group: Weapon
                Levels:
                  - Level: 1
                    RefineLevels:
                      - Level: 1
                        Bonus: 200
                        Chances:
                          - Type: NotARealType
                            Material: TestOre
              - Group: Armor
                Levels:
                  - Level: 1
                    RefineLevels:
                      - Level: 1
                        Bonus: 100
            """;
        Assert.Throws<ArgumentException>(() => RefineDataCompiler.BuildTable(_ => badType, FixtureMaterialResolver));
    }

    [Fact]
    public void Generate_Cost_ReturnsNullForARefineLevelWithNoChances()
    {
        var table = RefineDataCompiler.BuildTable(_ => FixtureWithChances, FixtureMaterialResolver);
        var generated = RefineDataCompiler.Generate(table, "abc");
        Assert.Contains("internal static (uint Rate, uint Price, int MaterialItemId, uint BreakingRate, uint DowngradeAmount)? Cost(", generated);
    }

    [Fact]
    public void RealPinnedData_SwordWeaponLevel1Refine1_NormalCostMatchesPinnedRefineYaml()
    {
        // Pinned db/re/refine.yml Group: Weapon, Level: 1 (Sword's WeaponLevel), RefineLevels[Level: 1],
        // Chances[Type: Normal]: Rate 10000, Price 50, Material Phracon (direct inspection, not re-derived
        // from the generated table under test).
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        var normal = table["Weapon"][1][1].Costs!["Normal"];
        Assert.Equal(10000u, normal.Rate);
        Assert.Equal(50u, normal.Price);
        Assert.True(normal.MaterialItemId > 0); // resolved Phracon's real item_db Id (confirms the resolver ran).
    }

    [Fact]
    public void BuildTable_ExcludesShadowGroups()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture, NoMaterialsExpected);
        Assert.False(table.ContainsKey("Shadow_Weapon"));
        Assert.False(table.ContainsKey("Shadow_Armor"));
    }

    [Fact]
    public void BuildTable_MissingWeaponOrArmorGroup_Throws()
    {
        const string noArmor = """
            Header:
              Type: REFINE_DB
              Version: 2
            Body:
              - Group: Weapon
                Levels:
                  - Level: 1
                    RefineLevels:
                      - Level: 1
                        Bonus: 100
            """;
        Assert.Throws<ArgumentException>(() => RefineDataCompiler.BuildTable(_ => noArmor, NoMaterialsExpected));
    }

    [Fact]
    public void Generate_Bonus_ReturnsZeroForRefineZero()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture, NoMaterialsExpected);
        var generated = RefineDataCompiler.Generate(table, "abc");
        Assert.Contains("if (refine <= 0) return 0;", generated);
    }

    [Fact]
    public void Generate_EmitsExactWeaponAndArmorBonusValues()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture, NoMaterialsExpected);
        var generated = RefineDataCompiler.Generate(table, "abc");
        Assert.Contains("[\"Weapon\"]", generated);
        Assert.Contains("[\"Armor\"]", generated);
        Assert.DoesNotContain("Shadow_Weapon", generated);
        Assert.Contains("[1] = (200, 0),", generated);
        Assert.Contains("[2] = (400, 300),", generated);
    }

    [Fact]
    public void RealPinnedData_WeaponLevel1Refine1Bonus_Matches200()
    {
        // Pinned db/re/refine.yml Group: Weapon, Levels[Level: 1].RefineLevels[Level: 1].Bonus: 200.
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        Assert.Equal(200u, table["Weapon"][1][1].Bonus);
        Assert.Equal(400u, table["Weapon"][1][2].Bonus);
    }

    [Fact]
    public void RealPinnedData_ArmorLevel1Refine1Bonus_Matches100()
    {
        // Pinned db/re/refine.yml Group: Armor, Levels[Level: 1].RefineLevels[Level: 1].Bonus: 100.
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        Assert.Equal(100u, table["Armor"][1][1].Bonus);
        Assert.Equal(200u, table["Armor"][1][2].Bonus);
    }

    // Pinned db/re/refine.yml Group: Weapon, Level: 1 (Sword's WeaponLevel): RandomBonus first appears
    // at RefineLevels[Level: 8] (300), rising through refine 20. Below refine 8 it is absent/zero -
    // the live player MATK overrefine widening (status.cpp:6325-6327) only applies from refine 8 up for
    // a level-1 weapon, which is why VF-23's refine-5 case shows no overrefine term but refine-10 does.
    [Fact]
    public void RealPinnedData_WeaponLevel1_RandomBonusIsZeroBeforeRefineEightThenRises()
    {
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        var level1 = table["Weapon"][1];
        for (var refine = 1; refine <= 7; refine++) Assert.Equal(0u, level1[refine].RandomBonusMax);
        Assert.Equal(300u, level1[8].RandomBonusMax);
        Assert.Equal(900u, level1[10].RandomBonusMax);
    }

    // Confirms a lookup miss on refineLevel can never legitimately occur for any tabulated item
    // level in the real pinned data (see GeneratedRefineBonus.Bonus's own doc comment): every
    // Weapon level 1-5 and Armor level 1-2 fully covers refine 1-20 with no gaps.
    [Fact]
    public void RealPinnedData_EveryTabulatedLevelCoversAllRefineLevelsOneToTwenty()
    {
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        foreach (var (group, levels) in table)
            foreach (var (level, refineLevels) in levels)
                for (var refine = 1; refine <= 20; refine++)
                    Assert.True(refineLevels.ContainsKey(refine), $"{group} level {level} is missing RefineLevel {refine}.");
    }

    [Fact]
    public void RealPinnedData_WeaponLevelsAreExactlyOneThroughFive()
    {
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        Assert.Equal([1, 2, 3, 4, 5], table["Weapon"].Keys.OrderBy(k => k));
    }

    [Fact]
    public void RealPinnedData_ArmorLevelsAreExactlyOneAndTwo()
    {
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        Assert.Equal([1, 2], table["Armor"].Keys.OrderBy(k => k));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
