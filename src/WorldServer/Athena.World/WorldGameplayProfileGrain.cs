using Athena.Net.World.Contracts;
using Orleans;

namespace Athena.Net.World;

// See IWorldGameplayProfileGrain. Stateless: the answer always comes from the hosting silo's deployment configuration, so a
// re-created activation (idle collection, silo failure and re-activation on another silo) recovers the identical profile with no
// stored state. Consistency between silos is enforced at silo startup (AddWorldGameplayProfile).
public sealed class WorldGameplayProfileGrain(WorldGameplayProfileOptions configured) : Grain, IWorldGameplayProfileGrain
{
    public Task<WorldGameplayProfileRegistration> RegisterAsync(WorldGameplayProfile requested)
    {
        if (string.IsNullOrWhiteSpace(requested.Region) || string.IsNullOrWhiteSpace(requested.RuleSet))
            throw new ArgumentException("A gameplay profile needs a region and a ruleset.", nameof(requested));
        return Task.FromResult(new WorldGameplayProfileRegistration(configured.Profile.Equals(requested), configured.Profile));
    }
}
