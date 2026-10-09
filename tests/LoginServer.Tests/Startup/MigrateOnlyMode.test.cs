using Microsoft.EntityFrameworkCore;
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

    private sealed class NoMigrationsContext(DbContextOptions<NoMigrationsContext> options) : DbContext(options);

    [Fact]
    public void HasMigrations_ReturnsFalse_WhenContextHasNoRegisteredMigrations()
    {
        using var db = new NoMigrationsContext(
            new DbContextOptionsBuilder<NoMigrationsContext>().UseSqlite("Data Source=:memory:").Options);

        Assert.False(MigrateOnlyMode.HasMigrations(db, "Test DB"));
    }

    [Fact]
    public void HasMigrations_ReturnsTrue_ForBothLoginServerContexts()
    {
        using var login = new Athena.Net.LoginServer.Db.LoginDbContext(
            new DbContextOptionsBuilder<Athena.Net.LoginServer.Db.LoginDbContext>().UseSqlServer("Server=unused").Options,
            new Athena.Net.LoginServer.Db.LoginDbTableNames());
        using var identity = new Athena.Net.LoginServer.Db.Identity.AthenaIdentityDbContext(
            new DbContextOptionsBuilder<Athena.Net.LoginServer.Db.Identity.AthenaIdentityDbContext>().UseSqlServer("Server=unused").Options);

        Assert.True(MigrateOnlyMode.HasMigrations(login, "DB"));
        Assert.True(MigrateOnlyMode.HasMigrations(identity, "Identity DB"));
    }
}
