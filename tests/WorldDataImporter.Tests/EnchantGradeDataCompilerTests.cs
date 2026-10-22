using Athena.WorldCompiler.Generation;

public sealed class EnchantGradeDataCompilerTests
{
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
        var table = EnchantGradeDataCompiler.BuildTable(_ => Fixture);
        Assert.Equal(10u, table["Armor"][2]["None"]);
        Assert.Equal(30u, table["Armor"][2]["D"]);
        Assert.Equal(10u, table["Weapon"][5]["None"]);
        Assert.Equal(100u, table["Weapon"][5]["B"]);
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
        Assert.Throws<ArgumentException>(() => EnchantGradeDataCompiler.BuildTable(_ => noArmor));
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
        Assert.Throws<ArgumentException>(() => EnchantGradeDataCompiler.BuildTable(_ => badGrade));
    }

    [Fact]
    public void Generate_EmitsExactWeaponAndArmorBonusValues()
    {
        var table = EnchantGradeDataCompiler.BuildTable(_ => Fixture);
        var generated = EnchantGradeDataCompiler.Generate(table, "abc");
        Assert.Contains("[\"Weapon\"]", generated);
        Assert.Contains("[\"Armor\"]", generated);
        Assert.Contains("[\"None\"] = 10,", generated);
        Assert.Contains("[\"B\"] = 100,", generated);
    }

    [Fact]
    public void Generate_Bonus_ReturnsZeroForAnUnresolvedKey()
    {
        var table = EnchantGradeDataCompiler.BuildTable(_ => Fixture);
        var generated = EnchantGradeDataCompiler.Generate(table, "abc");
        Assert.Contains("grades.TryGetValue(grade, out var bonus) ? bonus : 0", generated);
    }

    [Fact]
    public void RealPinnedData_WeaponLevel5_BonusMatchesPinnedEnchantgradeYaml()
    {
        // Pinned db/re/enchantgrade.yml Type: Weapon, Levels[Level: 5].Grades: None/D/C/B bonuses (direct
        // inspection, not re-derived from the generated table under test). No Grade: A is tabulated at all.
        var root = FindRepositoryRoot();
        var table = EnchantGradeDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        var weapon5 = table["Weapon"][5];
        Assert.Equal(10u, weapon5["None"]);
        Assert.Equal(30u, weapon5["D"]);
        Assert.Equal(50u, weapon5["C"]);
        Assert.Equal(100u, weapon5["B"]);
        Assert.False(weapon5.ContainsKey("A"));
    }

    [Fact]
    public void RealPinnedData_ArmorLevel2_BonusMatchesPinnedEnchantgradeYaml()
    {
        var root = FindRepositoryRoot();
        var table = EnchantGradeDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        var armor2 = table["Armor"][2];
        Assert.Equal(10u, armor2["None"]);
        Assert.Equal(30u, armor2["D"]);
        Assert.Equal(50u, armor2["C"]);
        Assert.Equal(100u, armor2["B"]);
        Assert.False(armor2.ContainsKey("A"));
    }

    [Fact]
    public void RealPinnedData_OnlyWeaponLevel5AndArmorLevel2AreTabulated()
    {
        // Enchant grade is a top-tier-only mechanic in the real pinned data - every OTHER weapon/armor level
        // legitimately has no entry at all (see EnchantGradeDataCompiler's own doc comment).
        var root = FindRepositoryRoot();
        var table = EnchantGradeDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        Assert.Equal([5], table["Weapon"].Keys.OrderBy(k => k));
        Assert.Equal([2], table["Armor"].Keys.OrderBy(k => k));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
