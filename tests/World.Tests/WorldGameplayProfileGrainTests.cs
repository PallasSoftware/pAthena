using Athena.Net.World.Contracts;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// One Orleans world serves one gameplay profile: the first registration fixes it, a different one is rejected.
public sealed class WorldGameplayProfileGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { _cluster = new TestClusterBuilder().Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldGameplayProfileGrain Grain() => _cluster.GrainFactory.GetGrain<IWorldGameplayProfileGrain>(WorldGameplayProfileGrainKey.WellKnownKey);

    [Fact]
    public async Task FirstRegistrationIsAccepted_AndTheSameProfileIsAcceptedAgain()
    {
        var iro = new WorldGameplayProfile("Iro", "Renewal");

        var first = await Grain().RegisterAsync(iro);
        var again = await Grain().RegisterAsync(iro);

        Assert.True(first.Accepted);
        Assert.True(again.Accepted);
        Assert.Equal(iro, again.Established);
    }

    [Fact]
    public async Task ADifferentRegionOrRuleSet_IsRejected_AndTheEstablishedProfileIsReported()
    {
        var established = new WorldGameplayProfile("Kro", "Renewal");
        Assert.True((await Grain().RegisterAsync(established)).Accepted);

        var differentRegion = await Grain().RegisterAsync(new WorldGameplayProfile("Iro", "Renewal"));
        var differentRuleSet = await Grain().RegisterAsync(new WorldGameplayProfile("Kro", "PreRenewal"));

        Assert.False(differentRegion.Accepted);
        Assert.False(differentRuleSet.Accepted);
        Assert.Equal(established, differentRegion.Established);
        Assert.Equal(established, differentRuleSet.Established);
        Assert.True((await Grain().RegisterAsync(established)).Accepted); // the established one is unaffected
    }

    [Theory]
    [InlineData("", "Renewal")]
    [InlineData("Iro", " ")]
    public async Task IncompleteProfile_IsRejectedAsInvalid(string region, string ruleSet)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Grain().RegisterAsync(new WorldGameplayProfile(region, ruleSet)));
    }
}
