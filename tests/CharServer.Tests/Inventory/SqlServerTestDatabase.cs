using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.CharServer.Tests.Inventory;

/// <summary>
/// A throw-away, fully migrated CharServer database on the disposable SQL Server named by ATHENA_NET_TEST_SQLSERVER
/// (see docs/migrations.md and the required Validate workflow, which provides one). Real transactions, real constraints.
/// </summary>
internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private static string ServerConnection => Environment.GetEnvironmentVariable(Tests.Db.SqlServerFactAttribute.Variable)!;

    public string Name { get; } = "pathena_invtest_" + Guid.NewGuid().ToString("N");
    public string ConnectionString => new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = Name }.ConnectionString;

    public static async Task<SqlServerTestDatabase> CreateAsync()
    {
        var database = new SqlServerTestDatabase();
        await ExecuteAsync($"CREATE DATABASE [{database.Name}]");
        var migrated = await DbSetup.MigrateAsync(new InterConfig(), new SecretConfig { CharDbProvider = "sqlserver", CharDbConnectionString = database.ConnectionString }, new CharDbTableNames());
        if (!migrated) throw new InvalidOperationException("Test database migration failed.");
        return database;
    }

    public CharDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CharDbContext>().UseSqlServer(ConnectionString, sql => sql.EnableRetryOnFailure()).Options, new CharDbTableNames());

    public ValueTask DisposeAsync() => new(ExecuteAsync($"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]"));

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(ServerConnection) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }
}
