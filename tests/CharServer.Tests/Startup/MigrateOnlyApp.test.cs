using Athena.Net.CharServer.Startup;

namespace Athena.Net.CharServer.Tests.Startup;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MigrateOnlyAppTests
{
    private static readonly string[] DbVariables =
        ["ConnectionStrings__CharDb", "ATHENA_NET_CHAR_DB_CONNECTION", "ATHENA_NET_CHAR_DB_PROVIDER"];

    /// <summary>
    /// With no database configured, --migrate-only must fail fast and return
    /// instead of starting the TCP server or the LoginServer connector (which
    /// would run until cancelled).
    /// </summary>
    [Fact]
    public async Task RunAsync_MigrateOnlyWithoutDatabase_ReturnsNonZero_WithoutStartingServer()
    {
        var saved = DbVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (var v in DbVariables) Environment.SetEnvironmentVariable(v, null);
        try
        {
            var exitCode = await CharServerApp.RunAsync(new[]
            {
                "--migrate-only",
                "--char-config", "missing/char.conf", "--inter-config", "missing/inter.conf",
                "--secrets", "missing/secret.json",
            }).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }
}
