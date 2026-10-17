using Athena.Net.MapServer.Gameplay.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

public sealed class GameplayRulesFactoryTests
{
    [Fact]
    public void DefaultOptions_AreRenewalAndIro()
    {
        var options = new GameplayOptions();
        Assert.Equal(RagnarokRuleSet.Renewal, options.RuleSet);
        Assert.Equal(GameplayRegion.Iro, options.Region);
    }

    private static string[] RuleTypeNames(GameplayRuleServices s) =>
        [s.Combat.GetType().Name, s.Stats.GetType().Name, s.Timing.GetType().Name, s.StatusEffects.GetType().Name, s.Experience.GetType().Name];

    [Fact]
    public void Create_Iro_RegistersOnlyIroConcreteImplementations()
    {
        var services = GameplayRulesFactory.Create(new GameplayOptions { Region = GameplayRegion.Iro });

        Assert.Equal(["IroCombatRules", "IroStatRules", "IroTimingRules", "IroStatusEffectRules", "IroExperienceRules"], RuleTypeNames(services));
        Assert.All(new object[] { services.Combat, services.Stats, services.Timing, services.StatusEffects, services.Experience },
            rules => Assert.EndsWith(".Iro", rules.GetType().Namespace));
    }

    [Fact]
    public void Create_Kro_RegistersOnlyKroConcreteImplementations()
    {
        var services = GameplayRulesFactory.Create(new GameplayOptions { Region = GameplayRegion.Kro });

        Assert.Equal(["KroCombatRules", "KroStatRules", "KroTimingRules", "KroStatusEffectRules", "KroExperienceRules"], RuleTypeNames(services));
        Assert.All(new object[] { services.Combat, services.Stats, services.Timing, services.StatusEffects, services.Experience },
            rules => Assert.EndsWith(".Kro", rules.GetType().Namespace));
    }

    [Fact]
    public void Create_RegionsNeverShareAConcreteInstanceOrType()
    {
        var iro = GameplayRulesFactory.Create(new GameplayOptions { Region = GameplayRegion.Iro });
        var kro = GameplayRulesFactory.Create(new GameplayOptions { Region = GameplayRegion.Kro });

        Assert.NotEqual(iro.Combat.GetType(), kro.Combat.GetType());
        Assert.NotEqual(iro.Stats.GetType(), kro.Stats.GetType());
        Assert.NotEqual(iro.Timing.GetType(), kro.Timing.GetType());
        Assert.NotEqual(iro.StatusEffects.GetType(), kro.StatusEffects.GetType());
        Assert.NotEqual(iro.Experience.GetType(), kro.Experience.GetType());
    }

    [Fact]
    public void Create_UnknownRegion_FailsExplicitly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => GameplayRulesFactory.Create(new GameplayOptions { Region = (GameplayRegion)99 }));
        Assert.Contains("region", ex.Message);
    }

    [Theory]
    [InlineData(GameplayRegion.Iro)]
    [InlineData(GameplayRegion.Kro)]
    public void Create_PreRenewal_ThrowsNotSupported_DoesNotFallBackToRenewal_ForEveryRegion(GameplayRegion region)
    {
        var ex = Assert.Throws<NotSupportedException>(() => GameplayRulesFactory.Create(new GameplayOptions { RuleSet = RagnarokRuleSet.PreRenewal, Region = region }));

        Assert.Contains("Pre-Renewal", ex.Message);
        Assert.DoesNotContain("Renewal rules", ex.Message); // Guard against an accidental silent-fallback message.
    }

    [Fact]
    public void Create_UnknownRuleSet_Throws()
    {
        Assert.Throws<NotSupportedException>(() => GameplayRulesFactory.Create(new GameplayOptions { RuleSet = (RagnarokRuleSet)99 }));
    }

    // The host's existing service collection receives the region's concrete implementations once; consumers resolve abstractions.
    [Theory]
    [InlineData(GameplayRegion.Iro, "Iro")]
    [InlineData(GameplayRegion.Kro, "Kro")]
    public void AddGameplayRules_RegistersTheSelectedRegionsImplementationsAsAbstractions(GameplayRegion region, string prefix)
    {
        using var provider = new ServiceCollection().AddGameplayRules(new GameplayOptions { Region = region }).BuildServiceProvider();

        var bundle = provider.GetRequiredService<GameplayRuleServices>();
        Assert.Same(bundle.Combat, provider.GetRequiredService<ICombatRules>());
        Assert.Same(bundle.Stats, provider.GetRequiredService<IStatRules>());
        Assert.Same(bundle.Timing, provider.GetRequiredService<ITimingRules>());
        Assert.Same(bundle.StatusEffects, provider.GetRequiredService<IStatusEffectRules>());
        Assert.Same(bundle.Experience, provider.GetRequiredService<IExperienceRules>());
        Assert.StartsWith(prefix, provider.GetRequiredService<ICombatRules>().GetType().Name);
        Assert.StartsWith(prefix, provider.GetRequiredService<IExperienceRules>().GetType().Name);
    }

    [Fact]
    public void AddGameplayRules_UnsupportedSelection_FailsDuringRegistration_NotLater()
    {
        Assert.Throws<NotSupportedException>(() => new ServiceCollection().AddGameplayRules(new GameplayOptions { Region = (GameplayRegion)7 }));
        Assert.Throws<NotSupportedException>(() => new ServiceCollection().AddGameplayRules(new GameplayOptions { RuleSet = RagnarokRuleSet.PreRenewal }));
    }
}
