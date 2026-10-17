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

// Cluster-level behavior: silos must agree, and a failover to another silo keeps the profile.
public sealed class WorldGameplayProfileClusterTests
{
    // The profile the NEXT silo to be built should carry; tests set it before (re)starting a silo.
    private static WorldGameplayProfile s_nextSiloProfile = new("Iro", "Renewal");

    public sealed class PerSiloProfile : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.AddWorldGameplayProfile(new WorldGameplayProfileOptions(s_nextSiloProfile));
    }

    private static async Task<TestCluster> DeployAsync(WorldGameplayProfile profile, short silos)
    {
        s_nextSiloProfile = profile;
        var builder = new TestClusterBuilder(silos);
        builder.AddSiloBuilderConfigurator<PerSiloProfile>();
        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    private static readonly WorldGameplayProfile Iro = new("Iro", "Renewal");
    private static readonly WorldGameplayProfile Kro = new("Kro", "Renewal");

    [Fact]
    public async Task ASiloConfiguredDifferentlyFromTheCluster_FailsToStart()
    {
        var cluster = await DeployAsync(Iro, 1);
        try
        {
            s_nextSiloProfile = Kro;
            await Assert.ThrowsAnyAsync<Exception>(() => cluster.StartAdditionalSiloAsync());

            // The running cluster is unaffected and still serves the iRO profile.
            var grain = cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
            Assert.True((await grain.RegisterAsync(Iro)).Accepted);
            Assert.False((await grain.RegisterAsync(Kro)).Accepted);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task ASiloWithTheSameProfile_JoinsAndTheClusterKeepsOneProfile()
    {
        var cluster = await DeployAsync(Kro, 1);
        try
        {
            s_nextSiloProfile = Kro;
            await cluster.StartAdditionalSiloAsync();

            var grain = cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
            Assert.True((await grain.RegisterAsync(Kro)).Accepted);
            Assert.False((await grain.RegisterAsync(Iro)).Accepted);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    // Failover: stop the silo that hosts the profile grain; the grain re-activates on the surviving silo and still answers with
    // the configured profile (a first-registrant-wins design would have been empty here).
    [Fact]
    public async Task WhenTheHostingSiloStops_TheGrainFailsOverWithTheSameProfile()
    {
        var cluster = await DeployAsync(Iro, 2);
        try
        {
            var grain = cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);
            Assert.True((await grain.RegisterAsync(Iro)).Accepted);

            var management = cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            var host = (await management.GetSimpleGrainStatistics())
                .First(g => g.GrainType.Contains("WorldGameplayProfileGrain", StringComparison.OrdinalIgnoreCase) && g.ActivationCount > 0).SiloAddress;
            var hostSilo = cluster.GetActiveSilos().First(s => s.SiloAddress.Equals(host));
            var survivor = cluster.GetActiveSilos().First(s => !s.SiloAddress.Equals(host));

            await cluster.StopSiloAsync(hostSilo);

            var kro = await grain.RegisterAsync(Kro);
            var iro = await grain.RegisterAsync(Iro);
            Assert.False(kro.Accepted);
            Assert.Equal(Iro, kro.Established);
            Assert.True(iro.Accepted);
            Assert.Contains(cluster.GetActiveSilos(), s => s.SiloAddress.Equals(survivor.SiloAddress));
        }
        finally { await cluster.StopAllSilosAsync(); }
    }
}
