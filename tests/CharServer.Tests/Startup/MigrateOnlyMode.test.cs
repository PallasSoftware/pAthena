using Microsoft.EntityFrameworkCore;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Startup;

namespace Athena.Net.CharServer.Tests.Startup;

public sealed class MigrateOnlyModeTests
{
    [Fact]
    public async Task RunAsync_ReturnsZero_WhenMigrationSucceeds()
    {
        Assert.Equal(0, await MigrateOnlyMode.RunAsync(() => Task.FromResult(true)));
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_WhenMigrationFails()
    {
        Assert.NotEqual(0, await MigrateOnlyMode.RunAsync(() => Task.FromResult(false)));
    }

    private sealed class NoMigrationsContext(DbContextOptions<NoMigrationsContext> options) : DbContext(options);

    [Fact]
    public void HasMigrations_ReturnsFalse_WhenContextHasNoRegisteredMigrations()
    {
        using var db = new NoMigrationsContext(
            new DbContextOptionsBuilder<NoMigrationsContext>().UseSqlServer("Server=unused").Options);

        Assert.False(MigrateOnlyMode.HasMigrations(db, "Test DB"));
    }

    [Fact]
    public void HasMigrations_ReturnsTrue_ForCharDbContext()
    {
        using var db = new CharDbContext(
            new DbContextOptionsBuilder<CharDbContext>().UseSqlServer("Server=unused").Options,
            new CharDbTableNames());

        Assert.True(MigrateOnlyMode.HasMigrations(db, "DB"));
    }

    [Fact]
    public void Parse_MigrateOnlyFlag_SetsMigrateOnly_WithoutEnablingAutoMigrate()
    {
        var options = StartupOptions.Parse(new[] { "--migrate-only" });

        Assert.True(options.MigrateOnly);
        Assert.False(options.AutoMigrate);
    }

    [Fact]
    public void Parse_AutoMigrateFlag_DoesNotImplyMigrateOnly()
    {
        var options = StartupOptions.Parse(new[] { "--auto-migrate" });

        Assert.True(options.AutoMigrate);
        Assert.False(options.MigrateOnly);
    }
}
