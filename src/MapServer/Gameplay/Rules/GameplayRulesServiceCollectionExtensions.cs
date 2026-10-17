using Microsoft.Extensions.DependencyInjection;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Registers the region-specific concrete rule implementations with the host's existing service collection, once, at the
// composition root. GameplayRulesFactory validates the selection (unsupported ruleset/region throw here, at startup).
public static class GameplayRulesServiceCollectionExtensions
{
    public static IServiceCollection AddGameplayRules(this IServiceCollection services, GameplayOptions options)
    {
        var rules = GameplayRulesFactory.Create(options);
        services.AddSingleton(rules);
        services.AddSingleton(rules.Combat);
        services.AddSingleton(rules.Stats);
        services.AddSingleton(rules.Timing);
        services.AddSingleton(rules.StatusEffects);
        services.AddSingleton(rules.Experience);
        return services;
    }
}
