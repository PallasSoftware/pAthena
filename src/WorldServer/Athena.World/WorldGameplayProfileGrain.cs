using Athena.Net.World.Contracts;
using Orleans;

namespace Athena.Net.World;

// See IWorldGameplayProfileGrain. Orleans' single-threaded activation turns make register-or-compare race-free without locks.
public sealed class WorldGameplayProfileGrain : Grain, IWorldGameplayProfileGrain
{
    private WorldGameplayProfile? _established;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // The established profile is in-memory only, so the activation must not be idle-collected while the silo runs.
        DelayDeactivation(TimeSpan.MaxValue);
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<WorldGameplayProfileRegistration> RegisterAsync(WorldGameplayProfile requested)
    {
        if (string.IsNullOrWhiteSpace(requested.Region) || string.IsNullOrWhiteSpace(requested.RuleSet))
            throw new ArgumentException("A gameplay profile needs a region and a ruleset.", nameof(requested));
        _established ??= requested;
        return Task.FromResult(new WorldGameplayProfileRegistration(_established.Value.Equals(requested), _established.Value));
    }
}
