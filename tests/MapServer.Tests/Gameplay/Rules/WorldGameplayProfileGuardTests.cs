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

public sealed class WorldGameplayProfileMonitorTests
{
    private static readonly WorldGameplayProfile Iro = new("Iro", "Renewal");
    private static readonly WorldGameplayProfile Kro = new("Kro", "Renewal");

    private static (Athena.Net.MapServer.Startup.WorldGameplayProfileMonitor Monitor, Func<int> Stops) Create(
        Func<WorldGameplayProfile, Task<WorldGameplayProfileRegistration>> register)
    {
        var stops = 0;
        var monitor = new Athena.Net.MapServer.Startup.WorldGameplayProfileMonitor(register, new GameplayOptions(), _ => { }, () => stops++);
        return (monitor, () => stops);
    }

    [Fact]
    public async Task CompatibleWorldAfterReconnect_KeepsServing()
    {
        var (monitor, stops) = Create(p => Task.FromResult(new WorldGameplayProfileRegistration(true, p)));
        await monitor.VerifyAfterReconnectAsync();
        Assert.False(monitor.Failed);
        Assert.Equal(0, stops());
    }

    [Fact]
    public async Task RestartedWorldWithAnotherProfile_StopsMapServerOnce()
    {
        var (monitor, stops) = Create(_ => Task.FromResult(new WorldGameplayProfileRegistration(false, Kro)));
        await monitor.VerifyAfterReconnectAsync();
        await monitor.VerifyAfterReconnectAsync();
        Assert.True(monitor.Failed);
        Assert.Equal(1, stops());
    }

    [Fact]
    public async Task UnverifiableWorld_FailsClosed()
    {
        var (monitor, stops) = Create(_ => throw new TimeoutException());
        await monitor.VerifyAfterReconnectAsync();
        Assert.True(monitor.Failed);
        Assert.Equal(1, stops());
    }
}
