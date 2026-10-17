using Athena.Net.World.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Athena.Net.World;

// The World's own, immutable gameplay profile for this deployment. Every silo of one World must be started with the same
// values (ATHENA_WORLD_GAMEPLAY_REGION / ATHENA_WORLD_GAMEPLAY_RULESET); a silo whose profile differs from the cluster's fails
// to start (see AddWorldGameplayProfile). The World never learns its profile from a MapServer.
public sealed record WorldGameplayProfileOptions(WorldGameplayProfile Profile)
{
    public const string RegionVariable = "ATHENA_WORLD_GAMEPLAY_REGION";
    public const string RuleSetVariable = "ATHENA_WORLD_GAMEPLAY_RULESET";

    // Same defaults as MapServer (iRO, Renewal) so existing single-profile deployments keep working unchanged.
    public static WorldGameplayProfileOptions FromEnvironment(Func<string, string?> getVariable)
    {
        var region = Canonical(getVariable(RegionVariable), "Iro", ["Iro", "Kro"], RegionVariable);
        var ruleSet = Canonical(getVariable(RuleSetVariable), "Renewal", ["Renewal"], RuleSetVariable);
        return new WorldGameplayProfileOptions(new WorldGameplayProfile(region, ruleSet));
    }

    private static string Canonical(string? value, string fallback, string[] allowed, string variable)
    {
        if (value is null) return fallback;
        var match = allowed.FirstOrDefault(name => name.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException($"Invalid {variable} value '{value}'. Valid values: {string.Join(", ", allowed)}.");
    }
}

public static class WorldGameplayProfileSiloExtensions
{
    // Registers this silo's configured profile and the startup check that proves the whole cluster agrees on it: once the
    // silo is active it asks the (single, cluster-wide) profile grain, which answers from the configuration of the silo
    // that hosts the activation. A silo configured differently from the activation's host is rejected and does not start,
    // so mixed-profile silos cannot coexist, and no state needs to be persisted.
    public static ISiloBuilder AddWorldGameplayProfile(this ISiloBuilder siloBuilder, WorldGameplayProfileOptions options)
    {
        siloBuilder.ConfigureServices(services => services.AddSingleton(options));
        siloBuilder.AddStartupTask(async (services, cancellationToken) =>
        {
            var grain = services.GetRequiredService<IGrainFactory>().GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
            var result = await grain.RegisterAsync(options.Profile).WaitAsync(cancellationToken);
            if (!result.Accepted)
            {
                throw new InvalidOperationException(
                    $"This World silo is configured for gameplay region '{options.Profile.Region}' / ruleset '{options.Profile.RuleSet}', but the cluster " +
                    $"already runs region '{result.Established.Region}' / ruleset '{result.Established.RuleSet}'. All silos of one World must share the same " +
                    $"{WorldGameplayProfileOptions.RegionVariable} / {WorldGameplayProfileOptions.RuleSetVariable}.");
            }
        });
        return siloBuilder;
    }
}
