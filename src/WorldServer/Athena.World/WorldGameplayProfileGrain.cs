using Athena.Net.World.Contracts;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;

namespace Athena.Net.World;

// See IWorldGameplayProfileGrain. Stateless: the answer always comes from the hosting silo's deployment configuration, so a
// re-created activation (idle collection, silo failure and re-activation on another silo) recovers the identical profile with no
// stored state. It is only a secondary check: where it activates is a placement detail, so before answering it also requires
// every active peer silo to have published the same profile (silo metadata, see AddWorldGameplayProfile); a peer that differs
// or cannot be verified makes the call fail rather than answer.
public sealed class WorldGameplayProfileGrain(
    WorldGameplayProfileOptions configured, ISiloMetadataCache cache, ISiloStatusOracle oracle, ILocalSiloDetails local) : Grain, IWorldGameplayProfileGrain
{
    public async Task<WorldGameplayProfileRegistration> RegisterAsync(WorldGameplayProfile requested)
    {
        if (string.IsNullOrWhiteSpace(requested.Region) || string.IsNullOrWhiteSpace(requested.RuleSet))
            throw new ArgumentException("A gameplay profile needs a region and a ruleset.", nameof(requested));
        await WorldGameplayProfilePeers.VerifyAsync(configured, cache, oracle, local.SiloAddress, CancellationToken.None);
        return new WorldGameplayProfileRegistration(configured.Profile.Equals(requested), configured.Profile);
    }
}
