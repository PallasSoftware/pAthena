using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Runtime;

namespace Athena.Net.LoginServer.Tests.Runtime;

/// <summary>
/// Runs only when ATHENA_NET_TEST_SQLSERVER holds a connection string to a
/// DISPOSABLE SQL Server whose login may create/drop databases (for example a
/// throwaway Docker container); otherwise reported as skipped. Real-database
/// integration test, not mocks.
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

public sealed class LoginMigrationIntegrationTests
{
    [SqlServerFact]
    public async Task MigrateOnly_FreshDatabase_MigratesBothContexts_AndIsRepeatable()
    {
        var server = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)!;
        var name = "pathena_logintest_" + Guid.NewGuid().ToString("N");
        var connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;
        await ExecuteAsync(server, $"CREATE DATABASE [{name}]");
        try
        {
            var interConfig = new InterConfig();
            var secrets = new SecretConfig { LoginDbProvider = "sqlserver", LoginDbConnectionString = connectionString };
            var services = new ServiceCollection();
            Assert.True(DbSetup.TryAddLoginDbContext(services, interConfig, secrets, new LoginDbTableNames()));
            Assert.True(IdentityDbSetup.TryAddAthenaIdentityDbContext(services, interConfig, secrets));
            await using var provider = services.BuildServiceProvider();

            Assert.True(await DbSetup.MigrateAsync(provider));
            Assert.True(await IdentityDbSetup.MigrateAsync(provider));
            // Repeat: succeeds and applies nothing new.
            Assert.True(await DbSetup.MigrateAsync(provider));
            Assert.True(await IdentityDbSetup.MigrateAsync(provider));

            await using var login = provider.GetRequiredService<IDbContextFactory<LoginDbContext>>().CreateDbContext();
            await using var identity = provider.GetRequiredService<IDbContextFactory<AthenaIdentityDbContext>>().CreateDbContext();
            // Both contexts share one __EFMigrationsHistory table, so the history is the union.
            var expected = login.Database.GetMigrations().Concat(identity.Database.GetMigrations()).Order().ToList();
            Assert.Equal(expected, (await login.Database.GetAppliedMigrationsAsync()).Order().ToList());
            Assert.Empty(await login.Database.GetPendingMigrationsAsync());
            Assert.Empty(await identity.Database.GetPendingMigrationsAsync());
            Assert.False(login.GetService<IMigrator>().HasPendingModelChanges());
            Assert.False(identity.GetService<IMigrator>().HasPendingModelChanges());
        }
        finally
        {
            await ExecuteAsync(server, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
        }
    }

    private static async Task ExecuteAsync(string server, string sql)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
