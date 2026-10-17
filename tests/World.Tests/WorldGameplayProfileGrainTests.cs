using Athena.Net.World.Contracts;
using Orleans.Hosting;
using Orleans.Runtime;
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
}
