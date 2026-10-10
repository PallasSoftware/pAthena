using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Runtime;

namespace Athena.Net.LoginServer.Startup;

/// <summary>
/// One-shot --migrate-only mode: applies pending EF Core migrations for both
/// DbContexts and returns an exit code, without starting listeners, the console
/// loop, self-tests or account provisioning. Suitable for any one-shot migration
/// step (local, Docker Compose, a CI job, a Kubernetes initContainer, ...).
/// <para>
/// Idempotent: MigrateAsync is a no-op when nothing is pending, and EF Core takes
/// a database-level lock while migrating, so a repeated or overlapping run is safe.
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

        LoginLogger.Error($"Migrate-only: {label} has no EF Core migrations registered; refusing to use EnsureCreated.");
        return false;
    }

    public static Task<int> RunAsync(bool loginDbAvailable, bool identityDbAvailable, IServiceProvider serviceProvider) =>
        RunAsync(
            loginDbAvailable,
            identityDbAvailable,
            () => DbSetup.MigrateAsync(serviceProvider),
            () => IdentityDbSetup.MigrateAsync(serviceProvider));

    public static async Task<int> RunAsync(
        bool loginDbAvailable,
        bool identityDbAvailable,
        Func<Task<bool>> migrateLoginDb,
        Func<Task<bool>> migrateIdentityDb)
    {
        // An unconfigured database is a failure here, unlike normal startup where
        // the server degrades to "no database": a migrator that did nothing must
        // not let the rollout proceed.
        if (!loginDbAvailable || !identityDbAvailable)
        {
            LoginLogger.Error("Migrate-only: database is not configured; nothing was migrated.");
            return 1;
        }

        if (!await migrateLoginDb())
        {
            LoginLogger.Error("Migrate-only: LoginDb migration failed.");
            return 1;
        }

        if (!await migrateIdentityDb())
        {
            LoginLogger.Error("Migrate-only: Identity DB migration failed.");
            return 1;
        }

        LoginLogger.Status("Migrate-only: all migrations applied.");
        return 0;
    }
}
