using Athena.Net.World.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;

namespace Athena.Net.World;

// The World's own, immutable gameplay profile for this deployment. Every silo of one World must be started with the same
// values (ATHENA_WORLD_GAMEPLAY_REGION / ATHENA_WORLD_GAMEPLAY_RULESET); a silo whose profile differs from the cluster's fails
// to start (see AddWorldGameplayProfile). The World never learns its profile from a MapServer. One deployment runs exactly one
// immutable profile for its lifetime - mixed-region clusters and concurrent conflicting-profile startups are not a supported
// deployment scenario, so a mismatched silo simply fails to start; there is no negotiation between differently configured silos.
public sealed record WorldGameplayProfileOptions(WorldGameplayProfile Profile)
{
    // Peer verification (see WorldGameplayProfilePeers): how long a peer's metadata may stay unavailable before it counts as a
    // failure (never as a match), and how often the running silo re-checks its peers.
    public TimeSpan PeerMetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(5);

    // Tests only: skips the join-time checks so the monitor's catch-up of the join race can be exercised deterministically.
    public bool VerifyAtStartup { get; init; } = true;

    // Called when the running silo finds a peer with another (or no) profile; the default stops the host.
    public Action<string>? OnPeerMismatch { get; init; }

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

// Each silo publishes its profile as immutable silo metadata. A peer is verified only by an explicit, equal value: a missing,
// empty or not-yet-fetched entry is "pending", and pending past the timeout is a failure, never a match.
public static class WorldGameplayProfilePeers
{
    public const string MetadataKey = "athena.world.gameplay-profile";

    public static string Encode(WorldGameplayProfile profile) => $"{profile.Region}/{profile.RuleSet}";

    public sealed record Result(string? Mismatch, IReadOnlyList<SiloAddress> Pending, SiloAddress? MismatchPeer = null);

    public static Result Check(WorldGameplayProfile own, SiloAddress self, IEnumerable<SiloAddress> activePeers, ISiloMetadataCache cache)
    {
        var expected = Encode(own);
        var pending = new List<SiloAddress>();
        foreach (var peer in activePeers.Where(peer => !peer.Equals(self)))
        {
            if (!cache.GetSiloMetadata(peer).Metadata.TryGetValue(MetadataKey, out var value) || string.IsNullOrEmpty(value)) pending.Add(peer);
            else if (value != expected) return new Result($"silo {peer} runs gameplay profile '{value}' but this silo is configured for '{expected}'", pending, peer);
        }
        return new Result(null, pending);
    }

    public static async Task VerifyAsync(WorldGameplayProfileOptions options, ISiloMetadataCache cache, ISiloStatusOracle oracle, SiloAddress self, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.PeerMetadataTimeout;
        while (true)
        {
            var result = Check(options.Profile, self, oracle.GetApproximateSiloStatuses(onlyActive: true).Keys, cache);
            if (result.Mismatch is not null) throw new InvalidOperationException($"World gameplay profile mismatch: {result.Mismatch}.");
            if (result.Pending.Count == 0) return;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException($"World gameplay profile could not be verified: no profile metadata from {string.Join(", ", result.Pending)} within {options.PeerMetadataTimeout}.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}

// Re-checks the peers while the silo runs, so a peer that joins after this silo's own startup check (and so was never seen by
// it) is still caught: a mismatched peer stops this silo closed instead of leaving a mixed cluster running undetected.
internal sealed class WorldGameplayProfileMonitor(
    WorldGameplayProfileOptions options, ISiloMetadataCache cache, ISiloStatusOracle oracle, ILocalSiloDetails local,
    IHostApplicationLifetime lifetime, ILogger<WorldGameplayProfileMonitor> logger) : BackgroundService
{
    private readonly Dictionary<SiloAddress, DateTime> _pendingSince = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.MonitorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var failure = Evaluate(DateTime.UtcNow);
                if (failure is null) continue;
                logger.LogCritical("World gameplay profile peer check failed: {Failure}", failure);
                if (options.OnPeerMismatch is { } callback) callback(failure); else lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    private string? Evaluate(DateTime now)
    {
        var peers = oracle.GetApproximateSiloStatuses(onlyActive: true).Keys;
        var result = WorldGameplayProfilePeers.Check(options.Profile, local.SiloAddress, peers, cache);
        if (result.Mismatch is not null) return result.Mismatch;
        foreach (var gone in _pendingSince.Keys.Except(result.Pending).ToList()) _pendingSince.Remove(gone);
        foreach (var peer in result.Pending)
        {
            if (!_pendingSince.TryAdd(peer, now) || now - _pendingSince[peer] < options.PeerMetadataTimeout) continue;
            return $"silo {peer} did not publish gameplay-profile metadata within {options.PeerMetadataTimeout}";
        }
        return null;
    }
}

public static class WorldGameplayProfileSiloExtensions
{
    // Registers this silo's configured profile and proves the cluster agrees on it, in two independent layers:
    // 1. Silo metadata (authoritative): the profile is published with UseSiloMetadata. At startup every active peer must be
    //    verified as equal - a mismatch, or metadata that stays unavailable past the timeout, fails startup. While running, a
    //    monitor repeats the check so a peer that joins later and mismatches is still caught (fail closed).
    // 2. The cluster-wide profile grain, which answers from the configuration of the silo hosting the activation. It is only a
    //    secondary check: where it is activated is a placement detail and proves nothing about the other silos.
    public static ISiloBuilder AddWorldGameplayProfile(this ISiloBuilder siloBuilder, WorldGameplayProfileOptions options)
    {
        siloBuilder.UseSiloMetadata(new Dictionary<string, string> { [WorldGameplayProfilePeers.MetadataKey] = WorldGameplayProfilePeers.Encode(options.Profile) });
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton(options);
            services.AddHostedService<WorldGameplayProfileMonitor>();
        });
        siloBuilder.AddStartupTask(async (services, cancellationToken) =>
        {
            if (!options.VerifyAtStartup) return;
            await WorldGameplayProfilePeers.VerifyAsync(options, services.GetRequiredService<ISiloMetadataCache>(),
                services.GetRequiredService<ISiloStatusOracle>(), services.GetRequiredService<ILocalSiloDetails>().SiloAddress, cancellationToken);
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
