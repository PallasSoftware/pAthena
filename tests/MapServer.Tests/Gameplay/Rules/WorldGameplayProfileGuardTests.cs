using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

public sealed class WorldGameplayProfileGuardTests
{
    [Fact]
    public void ProfileOf_CarriesRegionAndRuleSetByName()
    {
        Assert.Equal(new WorldGameplayProfile("Kro", "Renewal"), WorldGameplayProfileGuard.ProfileOf(new GameplayOptions { Region = GameplayRegion.Kro }));
        Assert.Equal(new WorldGameplayProfile("Iro", "Renewal"), WorldGameplayProfileGuard.ProfileOf(new GameplayOptions()));
    }

    [Fact]
    public async Task AcceptedRegistration_Passes_AndSendsThisDeploymentsProfile()
    {
        WorldGameplayProfile? sent = null;

        await WorldGameplayProfileGuard.EnsureCompatibleAsync(p => { sent = p; return Task.FromResult(new WorldGameplayProfileRegistration(true, p)); },
            new GameplayOptions { Region = GameplayRegion.Kro });

        Assert.Equal(new WorldGameplayProfile("Kro", "Renewal"), sent);
    }

    [Fact]
    public async Task RejectedRegistration_FailsStartupNamingBothProfiles()
    {
        var established = new WorldGameplayProfile("Kro", "Renewal");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WorldGameplayProfileGuard.EnsureCompatibleAsync(
            _ => Task.FromResult(new WorldGameplayProfileRegistration(false, established)), new GameplayOptions { Region = GameplayRegion.Iro }));

        Assert.Contains("Iro", ex.Message);
        Assert.Contains("Kro", ex.Message);
    }
}
