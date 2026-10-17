using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Startup handshake: this deployment's gameplay profile (region + ruleset) is registered with the shared Orleans world,
// which accepts the first one and rejects any different one. Called once at the composition root before serving traffic.
public static class WorldGameplayProfileGuard
{
    public static WorldGameplayProfile ProfileOf(GameplayOptions options) => new(options.Region.ToString(), options.RuleSet.ToString());

    public static async Task EnsureCompatibleAsync(Func<WorldGameplayProfile, Task<WorldGameplayProfileRegistration>> register, GameplayOptions options)
    {
        var requested = ProfileOf(options);
        var result = await register(requested);
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"This MapServer runs gameplay region '{requested.Region}' / ruleset '{requested.RuleSet}', but the shared World is already serving " +
                $"region '{result.Established.Region}' / ruleset '{result.Established.RuleSet}'. One world must not mix gameplay profiles; " +
                "align gameplay_region/gameplay_ruleset on every MapServer of this deployment.");
        }
    }
}
