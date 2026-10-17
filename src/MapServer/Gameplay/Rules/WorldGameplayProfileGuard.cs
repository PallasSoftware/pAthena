using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Handshake: this deployment's gameplay profile (region + ruleset) is checked against the shared Orleans world, whose profile is
// fixed by the World's own deployment configuration (never by the first MapServer). Called at startup and after every reconnect.
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
                $"This MapServer runs gameplay region '{requested.Region}' / ruleset '{requested.RuleSet}', but the shared World is configured for " +
                $"region '{result.Established.Region}' / ruleset '{result.Established.RuleSet}'. One world must not mix gameplay profiles; " +
                "align gameplay_region/gameplay_ruleset on every MapServer of this deployment.");
        }
    }
}
