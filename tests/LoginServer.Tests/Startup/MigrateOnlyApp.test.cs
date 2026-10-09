using Athena.Net.LoginServer.Startup;

namespace Athena.Net.LoginServer.Tests.Startup;

[Collection("LoginDbEnvironment")]
public sealed class MigrateOnlyAppTests
{
    private static readonly string[] DbVariables =
        ["ConnectionStrings__LoginDb", "ATHENA_NET_LOGIN_DB_CONNECTION", "ATHENA_NET_LOGIN_DB_PROVIDER"];

    /// <summary>
    /// With no database configured, --migrate-only must fail fast and return
    /// instead of reaching the self-test, account provisioning or the server loop
    /// (which would block or read stdin) even when those flags are also passed.
    /// </summary>
    [Fact]
    public async Task RunAsync_MigrateOnlyWithoutDatabase_ReturnsNonZero_AndSkipsOtherModes()
    {
        var saved = DbVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (var v in DbVariables) Environment.SetEnvironmentVariable(v, null);
        try
        {
            var exitCode = await LoginServerApp.RunAsync(new[]
            {
                "--migrate-only", "--self-test", "--create-account-username", "someone",
                "--login-config", "missing/login.conf", "--inter-config", "missing/inter.conf",
                "--secrets", "missing/secret.json", "--subnet-config", "missing/subnet.conf",
                "--login-msg-config", "missing/msg.conf",
            }).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }
}
