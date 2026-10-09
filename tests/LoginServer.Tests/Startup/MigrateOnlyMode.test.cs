using Athena.Net.LoginServer.Startup;

namespace Athena.Net.LoginServer.Tests.Startup;

public sealed class MigrateOnlyModeTests
{
    [Fact]
    public async Task RunAsync_ReturnsZero_AndMigratesBothContexts_WhenBothSucceed()
    {
        var calls = new List<string>();

        var exitCode = await MigrateOnlyMode.RunAsync(
            true, true,
            () => { calls.Add("login"); return Task.FromResult(true); },
            () => { calls.Add("identity"); return Task.FromResult(true); });

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "login", "identity" }, calls);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_AndSkipsIdentity_WhenLoginMigrationFails()
    {
        var identityRan = false;

        var exitCode = await MigrateOnlyMode.RunAsync(
            true, true,
            () => Task.FromResult(false),
            () => { identityRan = true; return Task.FromResult(true); });

        Assert.NotEqual(0, exitCode);
        Assert.False(identityRan);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_WhenIdentityMigrationFails()
    {
        var exitCode = await MigrateOnlyMode.RunAsync(
            true, true,
            () => Task.FromResult(true),
            () => Task.FromResult(false));

        Assert.NotEqual(0, exitCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RunAsync_ReturnsNonZero_WithoutMigrating_WhenDatabaseNotConfigured(bool loginAvailable, bool identityAvailable)
    {
        var migrated = false;

        var exitCode = await MigrateOnlyMode.RunAsync(
            loginAvailable, identityAvailable,
            () => { migrated = true; return Task.FromResult(true); },
            () => { migrated = true; return Task.FromResult(true); });

        Assert.NotEqual(0, exitCode);
        Assert.False(migrated);
    }
}
