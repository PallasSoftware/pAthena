using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Runtime;

namespace Athena.Net.CharServer.Tests.Db;

/// <summary>
/// Runs only when ATHENA_NET_TEST_SQLSERVER holds a connection string to a
/// DISPOSABLE SQL Server whose login may create/drop databases (for example a
/// throwaway Docker container). Each test creates and drops its own database;
/// otherwise the tests are reported as skipped. Real-database integration tests,
/// not mocks.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string Variable = "ATHENA_NET_TEST_SQLSERVER";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a disposable SQL Server connection string to run.";
        }
    }
}

public sealed class CharMigrationIntegrationTests
{
    private static string ServerConnection => Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)!;

    private sealed class TempDatabase : IAsyncDisposable
    {
        public string Name { get; } = "pathena_chartest_" + Guid.NewGuid().ToString("N");
        public string ConnectionString => new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = Name }.ConnectionString;

        public static async Task<TempDatabase> CreateAsync()
        {
            var db = new TempDatabase();
            await ExecuteAsync($"CREATE DATABASE [{db.Name}]");
            return db;
        }

        public ValueTask DisposeAsync() => new(ExecuteAsync($"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]"));

        private static async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = "master" }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static CharDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CharDbContext>().UseSqlServer(connectionString).Options, new CharDbTableNames());

    private static Task<bool> MigrateOnlyAsync(string connectionString) =>
        DbSetup.MigrateAsync(new InterConfig(), new SecretConfig { CharDbProvider = "sqlserver", CharDbConnectionString = connectionString }, new CharDbTableNames());

    [SqlServerFact]
    public async Task MigrateOnly_FreshDatabase_AppliesAllMigrations_AndIsRepeatable()
    {
        await using var temp = await TempDatabase.CreateAsync();

        Assert.True(await MigrateOnlyAsync(temp.ConnectionString));

        await using var db = NewContext(temp.ConnectionString);
        var expected = db.Database.GetMigrations().ToList();
        Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync()).ToList());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.GetService<IMigrator>().HasPendingModelChanges(), "model snapshot is out of sync with the model");

        // Second run: succeeds and changes nothing.
        Assert.True(await MigrateOnlyAsync(temp.ConnectionString));
        Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync()).ToList());
    }

    [SqlServerFact]
    public async Task MigrateOnly_ThenReadAndWrite_PreservesDataAndConcurrencyToken()
    {
        await using var temp = await TempDatabase.CreateAsync();
        Assert.True(await MigrateOnlyAsync(temp.ConnectionString));

        await using (var db = NewContext(temp.ConnectionString))
        {
            db.Characters.Add(new CharCharacter { Name = "Alpha", AccountId = 2000001, Zeny = 123456789, BaseLevel = 99 });
            await db.SaveChangesAsync();
        }

        // Re-running the migrator leaves existing rows untouched.
        Assert.True(await MigrateOnlyAsync(temp.ConnectionString));

        await using (var db = NewContext(temp.ConnectionString))
        {
            var row = await db.Characters.SingleAsync(c => c.Name == "Alpha");
            Assert.Equal(123456789L, (long)row.Zeny);
            row.Zeny = 5;
            row.GameplayStateVersion++;
            await db.SaveChangesAsync();
        }

        await using (var stale = NewContext(temp.ConnectionString))
        {
            var row = await stale.Characters.SingleAsync(c => c.Name == "Alpha");
            await using (var other = NewContext(temp.ConnectionString))
            {
                var same = await other.Characters.SingleAsync(c => c.Name == "Alpha");
                same.GameplayStateVersion++;
                await other.SaveChangesAsync();
            }

            row.GameplayStateVersion++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        }
    }

    [SqlServerFact]
    public async Task MigrateOnly_UpgradesDatabaseAtOlderMigration_PreservingRows()
    {
        await using var temp = await TempDatabase.CreateAsync();
        Assert.True(await MigrateOnlyAsync(temp.ConnectionString));

        await using (var db = NewContext(temp.ConnectionString))
        {
            db.Characters.Add(new CharCharacter { Name = "Legacy", AccountId = 7, Zeny = 42 });
            await db.SaveChangesAsync();

            // Step the schema back one migration (data in the older columns is kept), then
            // let the migrator bring it forward again.
            var all = db.Database.GetMigrations().ToList();
            Assert.True(all.Count >= 2);
            await db.GetService<IMigrator>().MigrateAsync(all[^2]);
            Assert.Single(await db.Database.GetPendingMigrationsAsync());
        }

        Assert.True(await MigrateOnlyAsync(temp.ConnectionString));

        await using var after = NewContext(temp.ConnectionString);
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
        var row = await after.Characters.SingleAsync(c => c.Name == "Legacy");
        Assert.Equal(42L, (long)row.Zeny);
    }
}
