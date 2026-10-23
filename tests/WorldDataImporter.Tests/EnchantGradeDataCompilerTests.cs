using Athena.WorldCompiler.Generation;

public sealed class EnchantGradeDataCompilerTests
{
    private static Func<string, int> NoMaterials() => _ => throw new InvalidOperationException("This fixture declares no Catalyst/Options block, so no material should ever be resolved.");

    private const string Fixture = """
        Header:
          Type: ENCHANTGRADE_DB
          Version: 3

        Body:
          - Type: Armor
            Levels:
              - Level: 2
                Grades:
                  - Grade: None
                    Bonus: 10
                  - Grade: D
                    Bonus: 30
          - Type: Weapon
            Levels:
              - Level: 5
                Grades:
                  - Grade: None
                    Bonus: 10
                  - Grade: B
                    Bonus: 100
        """;

    [Fact]
    public void BuildTable_ParsesWeaponAndArmorBonusValues()
    {
        var table = EnchantGradeDataCompiler.BuildTable(_ => Fixture, NoMaterials());
        Assert.Equal(10u, table["Armor"][2]["None"].Bonus);
        Assert.Equal(30u, table["Armor"][2]["D"].Bonus);
        Assert.Equal(10u, table["Weapon"][5]["None"].Bonus);
        Assert.Equal(100u, table["Weapon"][5]["B"].Bonus);
    }

    [Fact]
    public void BuildTable_MissingWeaponOrArmorType_Throws()
    {
        const string noArmor = """
            Header:
              Type: ENCHANTGRADE_DB
              Version: 3
            Body:
              - Type: Weapon
                Levels:
                  - Level: 5
                    Grades:
                      - Grade: None
                        Bonus: 10
            """;
        Assert.Throws<ArgumentException>(() => EnchantGradeDataCompiler.BuildTable(_ => noArmor, NoMaterials()));
    }

    [Fact]
    public void BuildTable_UnknownGrade_Throws()
    {
        const string badGrade = """
            Header:
              Type: ENCHANTGRADE_DB
              Version: 3
            Body:
              - Type: Weapon
                Levels:
                  - Level: 5
                    Grades:
                      - Grade: NotARealGrade
                        Bonus: 10
              - Type: Armor
                Levels:
                  - Level: 2
                    Grades:
                      - Grade: None
                        Bonus: 10
            """;
        Assert.Throws<ArgumentException>(() => EnchantGradeDataCompiler.BuildTable(_ => badGrade, NoMaterials()));
    }

    [Fact]
    public void BuildTable_ParsesChancesCatalystAndOptions()
    {
        const string withAction = """
            Header:
              Type: ENCHANTGRADE_DB
              Version: 3
            Body:
              - Type: Armor
                Levels:
                  - Level: 2
                    Grades:
                      - Grade: None
                        Chances:
                          - Refine: 9
                            Chance: 1000
                          - Refine: 10
                            Chance: 2000
                        Bonus: 10
                        Catalyst:
                            Item: Blessed_Etel_Dust
                            AmountPerStep: 1
                            MaximumSteps: 10
                            ChanceIncrease: 100
                        Options:
                          - Option: 0
                            Item: Etel_Skyblue_Jewel
                            Amount: 1
                            Zeny: 175000
                            BreakingRate: 10000
                          - Option: 1
                            Item: Etel_Skyblue_Jewel
                            Amount: 5
                            Zeny: 875000
              - Type: Weapon
                Levels:
                  - Level: 5
                    Grades:
                      - Grade: None
                        Bonus: 10
            """;
        var materials = new Dictionary<string, int>(StringComparer.Ordinal) { ["Blessed_Etel_Dust"] = 6240, ["Etel_Skyblue_Jewel"] = 7700 };
        var table = EnchantGradeDataCompiler.BuildTable(_ => withAction, name => materials[name]);
        var armorNone = table["Armor"][2]["None"];

        Assert.Equal(1000u, armorNone.ChancesByRefine[9]);
        Assert.Equal(2000u, armorNone.ChancesByRefine[10]);

        Assert.NotNull(armorNone.Catalyst);
        Assert.Equal(6240, armorNone.Catalyst!.Value.MaterialItemId);
        Assert.Equal(1u, armorNone.Catalyst.Value.AmountPerStep);
        Assert.Equal(10u, armorNone.Catalyst.Value.MaximumSteps);
        Assert.Equal(100u, armorNone.Catalyst.Value.ChanceIncrease);

        Assert.Equal(2, armorNone.Options.Count);
        Assert.Equal(new EnchantGradeDataCompiler.Option(7700, 1, 175000, 10000, 0), armorNone.Options[0]);
        Assert.Equal(new EnchantGradeDataCompiler.Option(7700, 5, 875000, 0, 0), armorNone.Options[1]);
    }

    [Fact]
    public void BuildTable_UnresolvableMaterialAegisName_Throws()
    {
        const string badMaterial = """
            Header:
              Type: ENCHANTGRADE_DB
              Version: 3
            Body:
              - Type: Armor
                Levels:
                  - Level: 2
                    Grades:
                      - Grade: None
                        Bonus: 10
                        Options:
                          - Option: 0
                            Item: Not_A_Real_Item
              - Type: Weapon
                Levels:
                  - Level: 5
                    Grades:
                      - Grade: None
                        Bonus: 10
            """;
        Assert.Throws<KeyNotFoundException>(() => EnchantGradeDataCompiler.BuildTable(_ => badMaterial, _ => throw new KeyNotFoundException()));
    }

    [Fact]
    public void Generate_EmitsExactWeaponAndArmorBonusValues()
    {
        var table = EnchantGradeDataCompiler.BuildTable(_ => Fixture, NoMaterials());
        var generated = EnchantGradeDataCompiler.Generate(table, "abc");
        Assert.Contains("[\"Weapon\"]", generated);
        Assert.Contains("[\"Armor\"]", generated);
        Assert.Contains("[\"None\"] = 10,", generated);
        Assert.Contains("[\"B\"] = 100,", generated);
    }

    [Fact]
    public void Generate_Bonus_ReturnsZeroForAnUnresolvedKey()
    {
        var table = EnchantGradeDataCompiler.BuildTable(_ => Fixture, NoMaterials());
        var generated = EnchantGradeDataCompiler.Generate(table, "abc");
        Assert.Contains("grades.TryGetValue(grade, out var bonus) ? bonus : 0", generated);
    }

    [Fact]
    public void Generate_EmitsChanceAtRefineCatalystForAndOptionCostMethods()
    {
        var table = EnchantGradeDataCompiler.BuildTable(_ => Fixture, NoMaterials());
        var generated = EnchantGradeDataCompiler.Generate(table, "abc");
        Assert.Contains("internal static uint ChanceAtRefine(", generated);
        Assert.Contains("internal static Catalyst? CatalystFor(", generated);
        Assert.Contains("internal static Option? OptionCost(", generated);
    }

    private static Func<string, int> RealMaterialResolver(string root)
    {
        var byAegis = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in new[] { "db/re/item_db_equip.yml", "db/re/item_db_etc.yml", "db/re/item_db_usable.yml" })
        {
            var text = File.ReadAllText(Path.Combine(root, "legacy/rathena", file));
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                text, @"^  - Id: (\d+)\n    AegisName: (\S+)$", System.Text.RegularExpressions.RegexOptions.Multiline))
                byAegis[match.Groups[2].Value] = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return name => byAegis[name];
    }

    [Fact]
    public void RealPinnedData_WeaponLevel5_BonusMatchesPinnedEnchantgradeYaml()
    {
        // Pinned db/re/enchantgrade.yml Type: Weapon, Levels[Level: 5].Grades: None/D/C/B bonuses (direct
        // inspection, not re-derived from the generated table under test). No Grade: A is tabulated at all.
        var root = FindRepositoryRoot();
        var table = EnchantGradeDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        var weapon5 = table["Weapon"][5];
        Assert.Equal(10u, weapon5["None"].Bonus);
        Assert.Equal(30u, weapon5["D"].Bonus);
        Assert.Equal(50u, weapon5["C"].Bonus);
        Assert.Equal(100u, weapon5["B"].Bonus);
        Assert.False(weapon5.ContainsKey("A"));
    }

    [Fact]
    public void RealPinnedData_ArmorLevel2_BonusMatchesPinnedEnchantgradeYaml()
    {
        var root = FindRepositoryRoot();
        var table = EnchantGradeDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        var armor2 = table["Armor"][2];
        Assert.Equal(10u, armor2["None"].Bonus);
        Assert.Equal(30u, armor2["D"].Bonus);
        Assert.Equal(50u, armor2["C"].Bonus);
        Assert.Equal(100u, armor2["B"].Bonus);
        Assert.False(armor2.ContainsKey("A"));
    }

    [Fact]
    public void RealPinnedData_OnlyWeaponLevel5AndArmorLevel2AreTabulated()
    {
        // Enchant grade is a top-tier-only mechanic in the real pinned data - every OTHER weapon/armor level
        // legitimately has no entry at all (see EnchantGradeDataCompiler's own doc comment).
        var root = FindRepositoryRoot();
        var table = EnchantGradeDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        Assert.Equal([5], table["Weapon"].Keys.OrderBy(k => k));
        Assert.Equal([2], table["Armor"].Keys.OrderBy(k => k));
    }

    // Pinned db/re/enchantgrade.yml Type: Armor, Level 2, Grade None: Chances[Refine 9]=1000, [Refine 20]=8000 -
    // direct inspection of the real pinned file (see the YAML excerpt read earlier in this session).
    [Fact]
    public void RealPinnedData_ArmorLevel2GradeNone_ChancesMatchPinnedEnchantgradeYaml()
    {
        var root = FindRepositoryRoot();
        var table = EnchantGradeDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)), RealMaterialResolver(root));
        var none = table["Armor"][2]["None"];
        Assert.Equal(1000u, none.ChancesByRefine[9]);
        Assert.Equal(2000u, none.ChancesByRefine[10]);
        Assert.Equal(7000u, none.ChancesByRefine[11]);
        Assert.Equal(8000u, none.ChancesByRefine[20]);
        Assert.NotNull(none.Catalyst);
        Assert.Equal(1u, none.Catalyst!.Value.AmountPerStep);
        Assert.Equal(10u, none.Catalyst.Value.MaximumSteps);
        Assert.Equal(100u, none.Catalyst.Value.ChanceIncrease);
        Assert.Equal(2, none.Options.Count);
        Assert.Equal(1u, none.Options[0].Amount);
        Assert.Equal(175000u, none.Options[0].Zeny);
        Assert.Equal(10000u, none.Options[0].BreakingRate);
        Assert.Equal(5u, none.Options[1].Amount);
        Assert.Equal(875000u, none.Options[1].Zeny);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
