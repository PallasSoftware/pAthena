using Microsoft.EntityFrameworkCore;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Startup;

/// <summary>
/// One-shot --migrate-only mode: applies pending CharDbContext migrations and
/// returns an exit code, without starting listeners, the LoginServer connection or
/// any background work. Suitable for any one-shot migration step (local, Docker
/// Compose, a CI job, a Kubernetes initContainer, ...).
/// <para>
/// Idempotent: MigrateAsync is a no-op when nothing is pending, and EF Core 10
/// takes a database-level lock while migrating, so a repeated or overlapping run
/// is safe.
/// </para>
/// </summary>
public static class MigrateOnlyMode
{
    /// <summary>Upper bound on time spent waiting for the database before failing.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Migration-only mode uses EF Core migrations exclusively: a context with none
    /// registered is an error, never an EnsureCreated schema that bypasses the
    /// migration history. Needs no database connection.
    /// </summary>
    public static bool HasMigrations(DbContext db, string label)
    {
        if (db.Database.GetMigrations().Any())
        {
            return true;
        }

        CharLogger.Error($"Migrate-only: {label} has no EF Core migrations registered; refusing to use EnsureCreated.");
        return false;
    }

    public static async Task<int> RunAsync(Func<Task<bool>> migrateCharDb)
    {
        if (!await migrateCharDb())
        {
            CharLogger.Error("Migrate-only: CharDb migration failed.");
            return 1;
        }

        CharLogger.Status("Migrate-only: all migrations applied.");
        return 0;
    }
}
