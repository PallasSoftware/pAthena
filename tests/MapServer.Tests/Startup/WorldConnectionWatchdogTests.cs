using Athena.Net.MapServer.Startup;

namespace Athena.Net.MapServer.Tests.Startup;

// MapServer ends (exit code 75) when it loses the World after having been connected to it, so that the orchestrator restarts it
// against the current World: a new process re-resolves the gateway, leases a new actor-id block and registers again.
public sealed class WorldConnectionWatchdogTests
{
    [Fact]
    public void ExitCode_IsTheTemporaryFailureCode_ThatTellsTheOrchestratorToRestart()
    {
        Assert.Equal(75, WorldConnectionWatchdog.ExitCode);
    }

    [Fact]
    public void BeforeTheFirstConnection_NothingIsLost()
    {
        var logged = new List<string>();
        var watchdog = new WorldConnectionWatchdog(logged.Add);

        watchdog.NotifyGatewayCountChanged(0, 0, connectionRecovered: false);
        watchdog.NotifyClusterConnectionLost();

        Assert.False(watchdog.WorldLost);
        Assert.False(watchdog.Lost.IsCompleted);
        Assert.Empty(logged);
    }

    [Fact]
    public void LosingTheLastGateway_AfterBeingConnected_IsFatal_AndLoggedOnce()
    {
        var logged = new List<string>();
        var watchdog = new WorldConnectionWatchdog(logged.Add);
        watchdog.NotifyGatewayCountChanged(1, 0, connectionRecovered: false);

        watchdog.NotifyGatewayCountChanged(0, 1, connectionRecovered: false);
        watchdog.NotifyClusterConnectionLost();

        Assert.True(watchdog.WorldLost);
        Assert.True(watchdog.Lost.IsCompleted);
        var line = Assert.Single(logged);
        Assert.Contains("World connection lost", line);
        Assert.Contains("restarted", line);
    }

    [Fact]
    public void ClusterConnectionLost_AfterBeingConnected_IsFatal()
    {
        var watchdog = new WorldConnectionWatchdog(_ => { });
        watchdog.NotifyGatewayCountChanged(1, 0, connectionRecovered: false);

        watchdog.NotifyClusterConnectionLost();

        Assert.True(watchdog.WorldLost);
    }

    [Fact]
    public void LosingOneOfSeveralGateways_IsNotFatal()
    {
        var watchdog = new WorldConnectionWatchdog(_ => { });
        watchdog.NotifyGatewayCountChanged(2, 0, connectionRecovered: false);

        watchdog.NotifyGatewayCountChanged(1, 2, connectionRecovered: false);

        Assert.False(watchdog.WorldLost);
    }

    [Fact]
    public void ADisabledWatchdog_NeverEndsMapServer()
    {
        var logged = new List<string>();
        var watchdog = new WorldConnectionWatchdog(logged.Add, enabled: false);
        watchdog.NotifyGatewayCountChanged(1, 0, connectionRecovered: false);

        watchdog.NotifyGatewayCountChanged(0, 1, connectionRecovered: false);
        watchdog.NotifyClusterConnectionLost();

        Assert.False(watchdog.WorldLost);
        Assert.Empty(logged);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("anything", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void TheEnvironmentVariable_DisablesItOnlyWithFalse(string? value, bool expected)
    {
        var previous = Environment.GetEnvironmentVariable(WorldConnectionWatchdog.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(WorldConnectionWatchdog.EnvironmentVariableName, value);
            Assert.Equal(expected, WorldConnectionWatchdog.IsEnabledByEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorldConnectionWatchdog.EnvironmentVariableName, previous);
        }
    }
}
