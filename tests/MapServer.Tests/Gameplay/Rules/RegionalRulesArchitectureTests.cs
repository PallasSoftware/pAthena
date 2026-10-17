using System.Text.RegularExpressions;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Iro;
using Athena.Net.MapServer.Gameplay.Rules.Kro;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

// Guards the regional-independence rules of Stage 15.R by scanning the actual production source and types.
public sealed class RegionalRulesArchitectureTests
{
    private static readonly string[] RuleKinds = ["Combat", "Stat", "Timing", "StatusEffect", "Experience", "Inventory"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "MapServer"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> RegionFiles(string region) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "MapServer", "Gameplay", "Rules", region), "*.cs");

    private static string StripComments(string source) => Regex.Replace(source, @"//[^\n]*", "");

    [Theory]
    [InlineData("Iro", "Kro")]
    [InlineData("Kro", "Iro")]
    public void RegionalImplementations_NeverReferenceTheOtherRegionOrBranchOnRegion(string region, string other)
    {
        var files = RegionFiles(region).ToArray();
        Assert.Equal(RuleKinds.Length, files.Length);
        foreach (var file in files)
        {
            var code = StripComments(File.ReadAllText(file));
            Assert.DoesNotContain(other, code);                          // no reference to the other region's types or namespace
            Assert.DoesNotContain("GameplayRegion", code);               // no region switches inside gameplay algorithms
            Assert.False(Regex.IsMatch(code, @"\b(isIro|isKro|IsIro|IsKro)\b"), $"{file} branches on a region flag");
            Assert.DoesNotContain("System.Reflection", code);            // no reflection / service location
            Assert.DoesNotContain("IServiceProvider", code);
        }
    }

    [Fact]
    public void RegionalImplementations_AreSealedAndDeriveOnlyFromObject_SoNothingInheritsAnotherRegionsEngine()
    {
        Type[] iro = [typeof(IroCombatRules), typeof(IroStatRules), typeof(IroTimingRules), typeof(IroStatusEffectRules), typeof(IroExperienceRules), typeof(IroInventoryRules)];
        Type[] kro = [typeof(KroCombatRules), typeof(KroStatRules), typeof(KroTimingRules), typeof(KroStatusEffectRules), typeof(KroExperienceRules), typeof(KroInventoryRules)];
        Assert.All(iro.Concat(kro), t =>
        {
            Assert.True(t.IsSealed, $"{t.Name} must be sealed");
            Assert.Equal(typeof(object), t.BaseType);
        });
        Assert.Empty(iro.Intersect(kro));
    }

    [Fact]
    public void RegionalConstructors_TakeOnlyNeutralAbstractions_NeverAnotherRegionsClass()
    {
        foreach (var type in new[] { typeof(IroExperienceRules), typeof(KroExperienceRules) })
            foreach (var parameter in type.GetConstructors().Single().GetParameters())
                Assert.True(parameter.ParameterType.IsInterface || parameter.ParameterType.IsGenericType, $"{type.Name} ctor parameter {parameter.Name} must be an abstraction");
    }

    // Consumers (everything outside the two region folders, the factory and the DI extension) depend on interfaces only.
    [Fact]
    public void GameplayConsumers_NeverNameAConcreteRegionalImplementation()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var allowed = new[] { "GameplayRulesFactory.cs" };
        var concrete = new Regex(@"\b(Iro|Kro)(" + string.Join('|', RuleKinds) + @")Rules\b");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/Gameplay/Rules/Iro/") || relative.Contains("/Gameplay/Rules/Kro/") || allowed.Contains(Path.GetFileName(file))) continue;
            Assert.False(concrete.IsMatch(StripComments(File.ReadAllText(file))), $"{relative} references a concrete regional rules class");
        }
    }

    // Gameplay formulas live only in the regional implementations: the former shared static calculators must stay gone.
    [Fact]
    public void NoSharedStaticGameplayCalculatorsRemain()
    {
        var src = Path.Combine(RepoRoot(), "src", "MapServer");
        var banned = new Regex(@"\b(StatusCalculator|DerivedStatCalculator|AttackDelayCalculator|MovementSpeedCalculator|PlayerDamageMotionCalculator|MobBasicAttackCalculator|WeaponAttackCalculator|RenewalBasicAttackRules|CharacterStatService)\b");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("Generated", ""))) continue;
            Assert.False(banned.IsMatch(StripComments(File.ReadAllText(file))), $"{Path.GetRelativePath(src, file)} references a retired shared calculator");
        }
    }
}
