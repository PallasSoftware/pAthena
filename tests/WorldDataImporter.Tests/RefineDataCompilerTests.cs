using Athena.WorldCompiler.Generation;

public sealed class RefineDataCompilerTests
{
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

    [Fact]
    public void BuildTable_ParsesWeaponAndArmorBonusValues()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture);
        Assert.Equal(200u, table["Weapon"][1][1]);
        Assert.Equal(400u, table["Weapon"][1][2]);
        Assert.Equal(100u, table["Weapon"][2][1]);
        Assert.Equal(100u, table["Armor"][1][1]);
    }

    [Fact]
    public void BuildTable_ExcludesShadowGroups()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture);
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
        Assert.Throws<ArgumentException>(() => RefineDataCompiler.BuildTable(_ => noArmor));
    }

    [Fact]
    public void Generate_Bonus_ReturnsZeroForRefineZero()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture);
        var generated = RefineDataCompiler.Generate(table, "abc");
        Assert.Contains("if (refine <= 0) return 0;", generated);
    }

    [Fact]
    public void Generate_EmitsExactWeaponAndArmorBonusValues()
    {
        var table = RefineDataCompiler.BuildTable(_ => Fixture);
        var generated = RefineDataCompiler.Generate(table, "abc");
        Assert.Contains("[\"Weapon\"]", generated);
        Assert.Contains("[\"Armor\"]", generated);
        Assert.DoesNotContain("Shadow_Weapon", generated);
        Assert.Contains("[1] = 200,", generated);
        Assert.Contains("[2] = 400,", generated);
    }

    [Fact]
    public void RealPinnedData_WeaponLevel1Refine1Bonus_Matches200()
    {
        // Pinned db/re/refine.yml Group: Weapon, Levels[Level: 1].RefineLevels[Level: 1].Bonus: 200.
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        Assert.Equal(200u, table["Weapon"][1][1]);
        Assert.Equal(400u, table["Weapon"][1][2]);
    }

    [Fact]
    public void RealPinnedData_ArmorLevel1Refine1Bonus_Matches100()
    {
        // Pinned db/re/refine.yml Group: Armor, Levels[Level: 1].RefineLevels[Level: 1].Bonus: 100.
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        Assert.Equal(100u, table["Armor"][1][1]);
        Assert.Equal(200u, table["Armor"][1][2]);
    }

    // Confirms a lookup miss on refineLevel can never legitimately occur for any tabulated item
    // level in the real pinned data (see GeneratedRefineBonus.Bonus's own doc comment): every
    // Weapon level 1-5 and Armor level 1-2 fully covers refine 1-20 with no gaps.
    [Fact]
    public void RealPinnedData_EveryTabulatedLevelCoversAllRefineLevelsOneToTwenty()
    {
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        foreach (var (group, levels) in table)
            foreach (var (level, refineLevels) in levels)
                for (var refine = 1; refine <= 20; refine++)
                    Assert.True(refineLevels.ContainsKey(refine), $"{group} level {level} is missing RefineLevel {refine}.");
    }

    [Fact]
    public void RealPinnedData_WeaponLevelsAreExactlyOneThroughFive()
    {
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        Assert.Equal([1, 2, 3, 4, 5], table["Weapon"].Keys.OrderBy(k => k));
    }

    [Fact]
    public void RealPinnedData_ArmorLevelsAreExactlyOneAndTwo()
    {
        var root = FindRepositoryRoot();
        var table = RefineDataCompiler.BuildTable(relative => File.ReadAllText(Path.Combine(root, "legacy/rathena", relative)));
        Assert.Equal([1, 2], table["Armor"].Keys.OrderBy(k => k));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
