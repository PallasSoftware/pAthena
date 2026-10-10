using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Startup;

namespace Athena.Net.LoginServer.Runtime;

/// <summary>
/// Registers and bootstraps <see cref="AthenaIdentityDbContext"/> (ASP.NET Core
/// Identity plus the strictly-1:1 AthenaGameAccount table). This is a separate
/// persistence boundary from <see cref="DbSetup"/>'s LoginDb (service accounts,
/// IP bans, audit logs), even though both currently point at the same physical
/// SQL Server database.
/// </summary>
public static class IdentityDbSetup
{
    /// <summary>
    /// Registers <see cref="AthenaIdentityDbContext"/> via AddDbContextFactory.
    /// This also registers the context itself as a scoped service (required by
    /// ASP.NET Core Identity's AddEntityFrameworkStores) - so the same
    /// registration serves both Identity's per-authentication-attempt scoped
    /// usage (see IdentityPlayerAuthenticationService) and short-lived,
    /// per-operation contexts for callers on a long-lived connection (see the
    /// CharServer Lc* AthenaGameAccount handlers in ClientSession), without ever
    /// tying one context instance to a TCP connection's lifetime.
    /// </summary>
    public static bool TryAddAthenaIdentityDbContext(IServiceCollection services, InterConfig interConfig, SecretConfig secrets)
    {
        var connectionString = DbSetup.ResolveConnectionString(interConfig, secrets);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LoginLogger.Error("Identity DB: no connection string configured (check conf/inter_athena.conf).");
            return false;
        }

        var dbProvider = DbSetup.ResolveDbProvider(interConfig, secrets);
        if (dbProvider != "sqlserver")
        {
            LoginLogger.Error($"Identity DB: unsupported provider '{dbProvider}'. LoginServer is SQL Server only.");
            return false;
        }

        services.AddDbContextFactory<AthenaIdentityDbContext>(optionsBuilder =>
        {
            optionsBuilder.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        return true;
    }

    /// <summary>
    /// Resolves the registered <see cref="IDbContextFactory{AthenaIdentityDbContext}"/>,
    /// applies migrations/waits for connectivity, and returns a short-lived-context
    /// factory delegate, mirroring DbSetup.CreateDbFactoryAsync.
    /// </summary>
    public static async Task<Func<AthenaIdentityDbContext?>> CreateDbFactoryAsync(IServiceProvider serviceProvider, bool autoMigrate)
    {
        try
        {
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<AthenaIdentityDbContext>>();
            Func<AthenaIdentityDbContext> factory = () => contextFactory.CreateDbContext();

            await ApplyMigrationsWithRetry(factory, autoMigrate);

            return factory;
        }
        catch (Exception ex)
        {
            LoginLogger.Error($"Identity DB: connection check failed ({ex.Message}).");
            return () => null;
        }
    }

    /// <summary>
    /// Migration-only entry point (--migrate-only); see DbSetup.MigrateAsync.
    /// </summary>
    public static async Task<bool> MigrateAsync(IServiceProvider serviceProvider)
    {
        try
        {
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<AthenaIdentityDbContext>>();
            Func<AthenaIdentityDbContext> factory = () => contextFactory.CreateDbContext();

            await using (var probe = factory())
            {
                if (!MigrateOnlyMode.HasMigrations(probe, "Identity DB"))
                {
                    return false;
                }
            }

            return await ApplyMigrationsWithRetry(factory, autoMigrate: true, MigrateOnlyMode.MaxWait);
        }
        catch (Exception ex)
        {
            LoginLogger.Error($"Identity DB: migration failed ({ex.Message}).");
            return false;
        }
    }

    private static async Task<bool> ApplyMigrationsWithRetry(Func<AthenaIdentityDbContext> factory, bool autoMigrate, TimeSpan? maxWait = null)
    {
        const int maxAttempts = 60;
        var delay = TimeSpan.FromSeconds(2);
        Exception? lastError = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        // maxWait (migrate-only) bounds the total time: a single attempt can itself take
        // a minute (e.g. rejected credentials), so the attempt count alone is no bound.
        for (var attempt = 1; attempt <= maxAttempts && (maxWait is null || elapsed.Elapsed < maxWait); attempt++)
        {
            try
            {
                await using var dbHandle = factory();

                if (autoMigrate)
                {
                    var hasMigrations = dbHandle.Database.GetMigrations().Any();
                    if (hasMigrations)
                    {
                        await dbHandle.Database.MigrateAsync();
                        LoginLogger.Status("Identity DB: migrations applied.");
                    }
                    else
                    {
                        await dbHandle.Database.EnsureCreatedAsync();
                        LoginLogger.Status("Identity DB: schema created (EnsureCreated).");
                    }
                }

                var canConnect = await dbHandle.Database.CanConnectAsync();
                if (canConnect)
                {
                    LoginLogger.Status("Identity DB: connected.");
                    return true;
                }

                lastError = new InvalidOperationException("Identity database not reachable.");
                LoginLogger.Status($"Identity DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                LoginLogger.Status($"Identity DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        LoginLogger.Error("Identity DB: unable to connect.");
        if (lastError != null)
        {
            LoginLogger.Error($"Identity DB: last error ({lastError.Message}).");
        }

        return false;
    }
}
