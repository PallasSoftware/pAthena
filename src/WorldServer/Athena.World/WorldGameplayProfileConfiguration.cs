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
// to start (see AddWorldGameplayProfile). The World never learns its profile from a MapServer.
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

    // OldestAgreeingPeer: the lowest-Generation peer found to AGREE with self, if any. A peer that agrees with self and started
    // before the mismatched peer is proof the mismatch is just a fellow, equally-unprivileged newcomer racing self - not a real
    // conflict with the established cluster - because an actually-established peer cannot simultaneously agree with self through
    // one peer and disagree through another (two already-established peers can't themselves disagree).
    public sealed record Result(string? Mismatch, IReadOnlyList<SiloAddress> Pending, SiloAddress? MismatchPeer = null, SiloAddress? OldestAgreeingPeer = null);

    public static Result Check(WorldGameplayProfile own, SiloAddress self, IEnumerable<SiloAddress> activePeers, ISiloMetadataCache cache)
    {
        var expected = Encode(own);
        var pending = new List<SiloAddress>();
        SiloAddress? mismatchPeer = null;
        SiloAddress? oldestAgreeing = null;
        string? mismatch = null;
        foreach (var peer in activePeers.Where(peer => !peer.Equals(self)))
        {
            if (!cache.GetSiloMetadata(peer).Metadata.TryGetValue(MetadataKey, out var value) || string.IsNullOrEmpty(value)) { pending.Add(peer); continue; }
            if (value == expected)
            {
                if (oldestAgreeing is null || peer.Generation < oldestAgreeing.Generation) oldestAgreeing = peer;
                continue;
            }

            // Prefer the OLDEST mismatched peer (lowest Generation = earliest start time), not just the first one enumeration
            // happens to surface. The peer that matters is whichever establishes the strongest claim to being "already there"
            // before this silo started; VerifyAsync/Evaluate below then decide whether that claim actually predates self.
            if (mismatchPeer is null || peer.Generation < mismatchPeer.Generation)
            {
                mismatchPeer = peer;
                mismatch = $"silo {peer} runs gameplay profile '{value}' but this silo is configured for '{expected}'";
            }
        }
        return mismatchPeer is null ? new Result(null, pending) : new Result(mismatch, pending, mismatchPeer, oldestAgreeing);
    }

    public static async Task VerifyAsync(WorldGameplayProfileOptions options, ISiloMetadataCache cache, ISiloStatusOracle oracle, SiloAddress self, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.PeerMetadataTimeout;
        while (true)
        {
            var result = Check(options.Profile, self, oracle.GetApproximateSiloStatuses(onlyActive: true).Keys, cache);
            if (result.Mismatch is not null)
            {
                // Join race: two differently configured silos can become mutually visible to each other while both are still
                // starting, each seeing the other as the mismatch. Self must fault ONLY when it is actually in conflict with an
                // established peer - never merely because Generation ordering between it and a fellow newcomer went one way or
                // the other. "Established" here means: the mismatched peer is older than self, AND self has no other peer it
                // agrees with that is at least as old as that mismatch (an agreeing peer that old proves self is already in step
                // with the real, established cluster, so the mismatch is just a concurrently-racing sibling, not a real conflict).
                if (!IsFaultingConflict(self, result))
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new InvalidOperationException($"World gameplay profile could not be verified: peer {result.MismatchPeer} still mismatched after {options.PeerMetadataTimeout}.");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    continue;
                }
                throw new InvalidOperationException($"World gameplay profile mismatch: {result.Mismatch}.");
            }
            if (result.Pending.Count == 0) return;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException($"World gameplay profile could not be verified: no profile metadata from {string.Join(", ", result.Pending)} within {options.PeerMetadataTimeout}.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    // Whether self must actually fault on the given Check() result, versus retry/ignore and let the real offender fault instead.
    // Two independent gates, both required:
    //  1. The mismatched peer must be "established" relative to self - started strictly before self (lower Generation). A peer
    //     that started at/after self is itself just a newcomer in this race, not evidence of a real conflict with history; it
    //     will fault on its own check instead. A genuine tie (identical Generation - both silos started at the exact same
    //     instant, so neither is established relative to the other) falls back to the ordinal tie-break so exactly one side of
    //     that pair still fails deterministically.
    //  2. Self must have no OTHER peer it agrees with that is at least as old as the mismatch. An agreeing peer that old proves
    //     self is already in step with the real established cluster - two already-established peers can never disagree with
    //     each other, so a mismatch that "loses" to such an agreement is just a fellow, equally-unprivileged newcomer that
    //     Check happened to surface, not a real conflict. Without this gate, a matching newcomer could still fault purely
    //     because a merely-concurrent, differently-configured newcomer happened to register a lower Generation.
    // Shared by the startup-time verify above and the running monitor below so a join race resolves identically in both places.
    internal static bool IsFaultingConflict(SiloAddress self, Result result)
    {
        var peer = result.MismatchPeer!;
        var establishedRelativeToSelf = self.Generation != peer.Generation
            ? self.Generation > peer.Generation
            : string.CompareOrdinal(self.ToString(), peer.ToString()) > 0;
        if (!establishedRelativeToSelf) return false;
        return result.OldestAgreeingPeer is not { } agreeing || agreeing.Generation > peer.Generation;
    }

    // Plain Generation-based "who yields" tie-break used only for the pending-metadata-timeout case, where the peer's profile
    // is unknown so the established-vs-newcomer distinction above does not apply - the peer isn't mismatched, it's just silent.
    internal static bool IsNewerThan(SiloAddress self, SiloAddress peer) =>
        self.Generation != peer.Generation ? self.Generation > peer.Generation : string.CompareOrdinal(self.ToString(), peer.ToString()) > 0;
}

// Re-checks the peers while the silo runs, which closes the join race (two differently configured silos starting at the same
// time each see the other as not yet active): both end up Active, see each other's metadata and the newer one stops, failing closed.
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

    // Self only stops when it is actually in conflict with an established peer (see IsFaultingConflict): a misconfigured
    // newcomer must never take down the established cluster, and when two silos joined together with no established side to
    // defer to, exactly one of them - the same one on both sides - stops.
    private string? Evaluate(DateTime now)
    {
        var peers = oracle.GetApproximateSiloStatuses(onlyActive: true).Keys;
        var result = WorldGameplayProfilePeers.Check(options.Profile, local.SiloAddress, peers, cache);
        if (result.Mismatch is not null) return WorldGameplayProfilePeers.IsFaultingConflict(local.SiloAddress, result) ? result.Mismatch : null;
        foreach (var gone in _pendingSince.Keys.Except(result.Pending).ToList()) _pendingSince.Remove(gone);
        foreach (var peer in result.Pending)
        {
            if (!_pendingSince.TryAdd(peer, now) || now - _pendingSince[peer] < options.PeerMetadataTimeout || !WorldGameplayProfilePeers.IsNewerThan(local.SiloAddress, peer)) continue;
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
    //    monitor repeats the check so peers that joined concurrently are still caught (fail closed).
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
