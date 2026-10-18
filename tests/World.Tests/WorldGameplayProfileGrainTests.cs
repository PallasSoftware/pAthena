using Athena.Net.World.Contracts;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// The World owns an immutable, deployment-configured gameplay profile; MapServers never define it.
public sealed class WorldGameplayProfileConfigurationTests
{
    private static Func<string, string?> Env(string? region = null, string? ruleSet = null) =>
        name => name switch { WorldGameplayProfileOptions.RegionVariable => region, WorldGameplayProfileOptions.RuleSetVariable => ruleSet, _ => null };

    [Fact]
    public void Defaults_AreIroAndRenewal() =>
        Assert.Equal(new WorldGameplayProfile("Iro", "Renewal"), WorldGameplayProfileOptions.FromEnvironment(Env()).Profile);

    [Theory]
    [InlineData("kRO", "Kro")]
    [InlineData("KRO", "Kro")]
    [InlineData(" iro ", "Iro")]
    public void Region_IsCaseInsensitiveAndCanonicalised(string value, string expected) =>
        Assert.Equal(expected, WorldGameplayProfileOptions.FromEnvironment(Env(region: value)).Profile.Region);

    [Theory]
    [InlineData("jRO", null)]
    [InlineData("", null)]
    [InlineData("1", null)]
    [InlineData(null, "PreRenewal")]
    [InlineData(null, "Bogus")]
    public void InvalidValues_FailStartup(string? region, string? ruleSet) =>
        Assert.Throws<InvalidOperationException>(() => WorldGameplayProfileOptions.FromEnvironment(Env(region, ruleSet)));
}

public sealed class WorldGameplayProfileGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private static readonly WorldGameplayProfile Iro = new("Iro", "Renewal");
    private static readonly WorldGameplayProfile Kro = new("Kro", "Renewal");

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<IroSilo>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    public sealed class IroSilo : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.AddWorldGameplayProfile(new WorldGameplayProfileOptions(Iro));
    }

    private IWorldGameplayProfileGrain Grain() => _cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);

    [Fact]
    public async Task ConfiguredProfileIsAccepted_AndAnyOtherIsRejected_NeverFirstWins()
    {
        // A kRO MapServer arriving FIRST must not define the world's profile: the World is configured for iRO.
        var kroFirst = await Grain().RegisterAsync(Kro);
        var iro = await Grain().RegisterAsync(Iro);

        Assert.False(kroFirst.Accepted);
        Assert.Equal(Iro, kroFirst.Established);
        Assert.True(iro.Accepted);
        Assert.Equal(Iro, iro.Established);
    }

    [Fact]
    public async Task RuleSetMismatch_IsRejected() =>
        Assert.False((await Grain().RegisterAsync(new WorldGameplayProfile("Iro", "PreRenewal"))).Accepted);

    [Theory]
    [InlineData("", "Renewal")]
    [InlineData("Iro", " ")]
    public async Task IncompleteProfile_IsRejectedAsInvalid(string region, string ruleSet) =>
        await Assert.ThrowsAnyAsync<Exception>(() => Grain().RegisterAsync(new WorldGameplayProfile(region, ruleSet)));

    // Grain re-activation: collect the activation, then ask again - the recreated grain recovers the profile from configuration.
    [Fact]
    public async Task AfterTheActivationIsCollected_TheRecreatedGrainRecoversTheConfiguredProfile()
    {
        Assert.True((await Grain().RegisterAsync(Iro)).Accepted);

        await _cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        var kro = await Grain().RegisterAsync(Kro);
        var iro = await Grain().RegisterAsync(Iro);
        Assert.False(kro.Accepted);
        Assert.Equal(Iro, kro.Established);
        Assert.True(iro.Accepted);
    }
}

// Cluster-level behavior: silos must agree (silo metadata is authoritative, the grain is secondary), and a failover keeps the profile.
public sealed class WorldGameplayProfileClusterTests
{
    private static readonly WorldGameplayProfile Iro = new("Iro", "Renewal");
    private static readonly WorldGameplayProfile Kro = new("Kro", "Renewal");

    // Options for the NEXT silo(s) to be built, consumed one per silo (null = a silo that publishes no profile at all).
    private static readonly System.Collections.Concurrent.ConcurrentQueue<WorldGameplayProfileOptions?> s_next = new();

    public sealed class PerSiloProfile : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            if (s_next.TryDequeue(out var options) && options is not null) siloBuilder.AddWorldGameplayProfile(options);
        }
    }

    private static WorldGameplayProfileOptions Opts(WorldGameplayProfile profile) =>
        new(profile) { PeerMetadataTimeout = TimeSpan.FromSeconds(3), MonitorInterval = TimeSpan.FromMilliseconds(100) };

    private static async Task<TestCluster> DeployAsync(WorldGameplayProfileOptions? first, short silos = 1)
    {
        s_next.Clear();
        for (var i = 0; i < silos; i++) s_next.Enqueue(first);
        var builder = new TestClusterBuilder(silos);
        builder.AddSiloBuilderConfigurator<PerSiloProfile>();
        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    // While a rejected silo is still being torn down the grain fails closed (it throws instead of answering); the answer itself
    // is only asserted once the call succeeds. A call that keeps failing for 15 s fails the test with the last exception.
    // After a rejected join the half-started silo can linger as an active peer until membership declares it dead; the grain then
    // fails closed. Either outcome is fine - answering "accepted" for the foreign profile is the only violation.
    private static async Task AssertNeverAcceptsAsync(TestCluster cluster, WorldGameplayProfile foreign)
    {
        var grain = cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
        WorldGameplayProfileRegistration? answer = null;
        try { answer = await grain.RegisterAsync(foreign); } catch (Exception) { /* failed closed */ }
        Assert.False(answer?.Accepted ?? false);
    }

    private static async Task<WorldGameplayProfileRegistration> RegisterAsync(TestCluster cluster, WorldGameplayProfile profile)
    {
        var grain = cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try { return await grain.RegisterAsync(profile); }
            catch (Exception) when (DateTime.UtcNow < deadline) { await Task.Delay(200); }
        }
    }

    [Fact]
    public async Task ASiloConfiguredDifferentlyFromTheCluster_FailsToStart()
    {
        var cluster = await DeployAsync(Opts(Iro));
        try
        {
            s_next.Enqueue(Opts(Kro));
            await Assert.ThrowsAnyAsync<Exception>(() => cluster.StartAdditionalSiloAsync());

            await AssertNeverAcceptsAsync(cluster, Kro);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Regression for the CI failure: with the grain deactivated, the joining silo must be rejected by the metadata check
    // even if the grain would activate on the joining silo itself and accept its own profile.
    [Fact]
    public async Task ADifferentlyConfiguredSiloJoiningAfterTheGrainWasDeactivated_StillFailsToStart()
    {
        var cluster = await DeployAsync(Opts(Iro));
        try
        {
            Assert.True((await RegisterAsync(cluster, Iro)).Accepted);
            await cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            s_next.Enqueue(Opts(Kro));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => cluster.StartAdditionalSiloAsync());

            Assert.Contains("mismatch", ex.ToString(), StringComparison.OrdinalIgnoreCase);
            await AssertNeverAcceptsAsync(cluster, Kro);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task ASiloWithTheSameProfile_JoinsAndTheClusterKeepsOneProfile()
    {
        var cluster = await DeployAsync(Opts(Kro));
        try
        {
            s_next.Enqueue(Opts(Kro));
            await cluster.StartAdditionalSiloAsync();

            Assert.Equal(2, cluster.GetActiveSilos().Count());
            Assert.True((await RegisterAsync(cluster, Kro)).Accepted);
            Assert.False((await RegisterAsync(cluster, Iro)).Accepted);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Fail closed: a peer that publishes no profile is never treated as a match.
    [Fact]
    public async Task APeerWithoutProfileMetadata_IsNeverTreatedAsAMatch()
    {
        var cluster = await DeployAsync(null);
        try
        {
            s_next.Enqueue(Opts(Iro));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => cluster.StartAdditionalSiloAsync());

            Assert.Contains("could not be verified", ex.ToString());
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Failover: stop the silo that hosts the profile grain; the grain re-activates on the survivor with the same profile, and the
    // dead peer (no longer active) does not make the peer verification fail.
    [Fact]
    public async Task WhenTheHostingSiloStops_TheGrainFailsOverWithTheSameProfile()
    {
        var cluster = await DeployAsync(Opts(Iro), 2);
        try
        {
            Assert.True((await RegisterAsync(cluster, Iro)).Accepted);

            var management = cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            var host = (await management.GetSimpleGrainStatistics())
                .First(g => g.GrainType.Contains("WorldGameplayProfileGrain", StringComparison.OrdinalIgnoreCase) && g.ActivationCount > 0).SiloAddress;
            var hostSilo = cluster.GetActiveSilos().First(s => s.SiloAddress.Equals(host));

            await cluster.StopSiloAsync(hostSilo);

            var kro = await RegisterAsync(cluster, Kro);
            Assert.False(kro.Accepted);
            Assert.Equal(Iro, kro.Established);
            Assert.True((await RegisterAsync(cluster, Iro)).Accepted);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Two differently configured silos starting at the same moment against an iRO cluster: exactly the kRO one is rejected.
    [Fact]
    public async Task ConcurrentIroAndKroStartups_OnlyTheMatchingSiloJoins()
    {
        var cluster = await DeployAsync(Opts(Iro));
        try
        {
            s_next.Enqueue(Opts(Iro));
            s_next.Enqueue(Opts(Kro));
            var starts = new[] { cluster.StartAdditionalSiloAsync(), cluster.StartAdditionalSiloAsync() };
            try { await Task.WhenAll(starts); } catch { /* the rejected silo's failure is asserted below */ }

            Assert.Equal(1, starts.Count(t => t.IsFaulted));
            Assert.Equal(2, cluster.GetActiveSilos().Count());
            await AssertNeverAcceptsAsync(cluster, Kro);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // The join race itself: a silo that skipped (or could not yet see its peers in) the startup check becomes active next to a
    // differently configured one. The running monitors must find the mismatch and stop exactly the NEWER silo - never leave a
    // mixed cluster running, and never stop the established silo because of a newcomer.
    [Fact]
    public async Task SilosThatBecameActiveTogetherWithDifferentProfiles_AreCaughtByTheMonitor_AndOnlyTheNewerStops()
    {
        var olderReports = new System.Collections.Concurrent.ConcurrentBag<string>();
        var newerReports = new System.Collections.Concurrent.ConcurrentBag<string>();
        var cluster = await DeployAsync(Opts(Iro) with { OnPeerMismatch = olderReports.Add });
        try
        {
            s_next.Enqueue(Opts(Kro) with { VerifyAtStartup = false, OnPeerMismatch = newerReports.Add });
            await cluster.StartAdditionalSiloAsync();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (newerReports.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(100);
            await Task.Delay(500); // give the older silo's monitor several more ticks in which it could wrongly report

            Assert.Single(newerReports);
            Assert.Contains("gameplay profile", newerReports.Single());
            Assert.Empty(olderReports);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Pins the exact problematic interleaving by construction instead of by timing luck: an established iRO silo (oldest
    // Generation), plus a matching iRO newcomer and a mismatched Kro newcomer starting concurrently, where the Kro newcomer's
    // Generation is made OLDER than the iRO newcomer's. Check() enumeration therefore surfaces the Kro newcomer - not the
    // established silo - as the iRO newcomer's "oldest mismatch", and a naive self-vs-mismatch Generation tie-break would make
    // the iRO newcomer fault even though it fully agrees with the established cluster.
    [Fact]
    public void MatchingNewcomer_IsNotAFaultingConflict_WhenOnlyAFellowNewcomerLooksOlderInEnumeration()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100); // oldest: already active before anyone else started
        var kroNewcomer = SiloAddress.New(endpoint, 150); // starts concurrently, but got a lower Generation than the iRO newcomer
        var iroNewcomer = SiloAddress.New(endpoint, 200); // the matching silo under test - self

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [kroNewcomer] = WorldGameplayProfilePeers.Encode(Kro),
        });

        var result = WorldGameplayProfilePeers.Check(Iro, iroNewcomer, [established, kroNewcomer, iroNewcomer], cache);

        // Confirms Check() does surface the Kro newcomer, not the established silo, as the "oldest mismatch" for this case -
        // i.e. this test exercises the actual enumeration-order hazard, not a scenario the old code handled by accident.
        Assert.Equal(kroNewcomer, result.MismatchPeer);
        Assert.Equal(established, result.OldestAgreeingPeer);

        // The pre-fix logic (a plain self-vs-MismatchPeer Generation tie-break) would see iroNewcomer (200) newer than
        // kroNewcomer (150) and incorrectly fault. The fix must recognise self already agrees with the established silo
        // (100), which predates the mismatch, so this is not a real conflict.
        Assert.False(WorldGameplayProfilePeers.IsFaultingConflict(iroNewcomer, result));
    }

    // The same interleaving from the Kro newcomer's own perspective: it has no older agreeing peer, so its mismatch against
    // the established silo (which predates it) is a real conflict and it must fault.
    [Fact]
    public void MismatchedNewcomer_IsAFaultingConflict_AgainstTheEstablishedSilo()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100);
        var kroNewcomer = SiloAddress.New(endpoint, 150);
        var iroNewcomer = SiloAddress.New(endpoint, 200);

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [iroNewcomer] = WorldGameplayProfilePeers.Encode(Iro),
        });

        var result = WorldGameplayProfilePeers.Check(Kro, kroNewcomer, [established, kroNewcomer, iroNewcomer], cache);

        Assert.Equal(established, result.MismatchPeer);
        Assert.Null(result.OldestAgreeingPeer);
        Assert.True(WorldGameplayProfilePeers.IsFaultingConflict(kroNewcomer, result));
    }

    // End-to-end: the matching iRO newcomer's VerifyAsync must complete (never throw) once the conflicting Kro newcomer has
    // left the picture - simulating the Kro newcomer having faulted on its own check - confirming the fix holds through the
    // full polling loop, not just the pure Check/IsFaultingConflict helpers.
    [Fact]
    public async Task MatchingNewcomer_VerifyAsyncSucceeds_OnceTheMismatchedFellowNewcomerIsGone()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var established = SiloAddress.New(endpoint, 100);
        var kroNewcomer = SiloAddress.New(endpoint, 150);
        var iroNewcomer = SiloAddress.New(endpoint, 200);

        var cache = new FakeSiloMetadataCache(new Dictionary<SiloAddress, string>
        {
            [established] = WorldGameplayProfilePeers.Encode(Iro),
            [kroNewcomer] = WorldGameplayProfilePeers.Encode(Kro),
        });
        // The first poll still sees the Kro newcomer active (the race); it is gone from the second poll onward, as if it had
        // just faulted on its own VerifyAsync and stopped - this is what lets our silo's retry loop converge deterministically.
        var oracle = new FakeSiloStatusOracle([[established, kroNewcomer, iroNewcomer], [established, iroNewcomer]]);
        var options = new WorldGameplayProfileOptions(Iro) { PeerMetadataTimeout = TimeSpan.FromSeconds(3) };

        // Must return (agree with the established cluster) rather than throw, which the pre-fix tie-break could do on the
        // very first poll before the Kro newcomer ever had a chance to disappear.
        await WorldGameplayProfilePeers.VerifyAsync(options, cache, oracle, iroNewcomer, CancellationToken.None);
    }

    private sealed class FakeSiloMetadataCache(Dictionary<SiloAddress, string> profiles) : ISiloMetadataCache
    {
        // SiloMetadata exposes Metadata as get-only with no public way to populate an instance outside Orleans' own
        // UseSiloMetadata pipeline; the backing field is set via reflection purely to build this test double.
        public SiloMetadata GetSiloMetadata(SiloAddress silo)
        {
            var metadata = new SiloMetadata();
            var backingField = typeof(SiloMetadata).GetField("<Metadata>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            backingField.SetValue(metadata, profiles.TryGetValue(silo, out var value)
                ? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(WorldGameplayProfilePeers.MetadataKey, value)
                : System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);
            return metadata;
        }
    }

    private sealed class FakeSiloStatusOracle : ISiloStatusOracle
    {
        private readonly IReadOnlyList<IReadOnlyList<SiloAddress>> _snapshots;
        private int _poll;

        public FakeSiloStatusOracle(IReadOnlyList<SiloAddress> active) : this([active]) { }

        // A sequence of active-peer snapshots, advancing one step per GetApproximateSiloStatuses call and staying on the
        // last snapshot once exhausted - lets a test simulate a peer disappearing between polls deterministically (by call
        // count, not timing).
        public FakeSiloStatusOracle(IReadOnlyList<IReadOnlyList<SiloAddress>> snapshots) => _snapshots = snapshots;

        private IReadOnlyList<SiloAddress> Active => _snapshots[Math.Min(_poll, _snapshots.Count - 1)];
        public SiloAddress SiloAddress => Active[0];
        public SiloStatus CurrentStatus => SiloStatus.Active;
        public string SiloName => "fake";
        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            var snapshot = Active.ToDictionary(s => s, _ => SiloStatus.Active);
            _poll++;
            return snapshot;
        }
        public SiloAddress[] GetActiveSilos() => [.. Active];
        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress) => SiloStatus.Active;
        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName) { siloName = "fake"; return true; }
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => true;
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => true;
        public bool IsDeadSilo(SiloAddress silo) => false;
        public bool IsFunctionalDirectory(SiloAddress siloAddress) => true;
    }
}
