using Orleans.Runtime;

namespace Athena.Net.MapServer.Startup;

/// <summary>
/// Ends MapServer when it loses its connection to the World, so that the orchestrator restarts it.
/// <para>
/// Why a restart and not a reconnect: the Orleans client reconnects by itself, but everything MapServer holds that came from the
/// World is bound to the World instance it talked to. Its NPC/warp actor-id block was leased from the World's in-memory
/// <c>ActorIdBlockAuthorityGrain</c> (a restarted World hands the same ids out again), its players' presences live in the
/// World's memory, and the gateway address was resolved once at startup (a replaced pod has another address, see
/// <see cref="OrleansGatewayResolver"/>). A fresh MapServer process re-resolves the name, re-leases a block and re-registers
/// with the CharServer, which is exactly the safe state; so losing the last gateway after having been connected is treated as
/// fatal (fail fast, never serve players from a stale state).
/// </para>
/// Before the first connection nothing is lost yet: the Orleans client keeps retrying while MapServer starts.
/// Set <c>ATHENA_NET_MAP_EXIT_ON_WORLD_LOSS=false</c> to keep the Orleans client's own reconnect only (development).
/// </summary>
public sealed class WorldConnectionWatchdog : IClusterConnectionStatusObserver
{
    /// <summary>Process exit code when MapServer ended because the World connection was lost (EX_TEMPFAIL).</summary>
    public const int ExitCode = 75;

    public const string EnvironmentVariableName = "ATHENA_NET_MAP_EXIT_ON_WORLD_LOSS";

    private readonly Action<string> _log;
    private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connected;

    public WorldConnectionWatchdog(Action<string> log, bool enabled = true)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Enabled = enabled;
    }

    public static bool IsEnabledByEnvironment() =>
        !string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariableName), "false", StringComparison.OrdinalIgnoreCase);

    public bool Enabled { get; }

    /// <summary>True once the World connection was lost and the process is meant to end.</summary>
    public bool WorldLost => _lost.Task.IsCompleted;

    /// <summary>Completes when the World connection was lost (never when the watchdog is disabled).</summary>
    public Task Lost => _lost.Task;

    /// <summary>Raised when the Orleans client reports a recovered connection to the World (the World may have restarted or been replaced).</summary>
    public event Action? ConnectionRecovered;

    public void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways, bool connectionRecovered)
    {
        if (currentNumberOfGateways > 0)
        {
            Interlocked.Exchange(ref _connected, 1);
            if (connectionRecovered) ConnectionRecovered?.Invoke();
            return;
        }

        Trigger("the last Orleans gateway of the World was lost");
    }

    public void NotifyClusterConnectionLost() => Trigger("the connection to the World cluster was lost");

    private void Trigger(string reason)
    {
        if (!Enabled || Volatile.Read(ref _connected) == 0)
        {
            return;
        }

        if (_lost.TrySetResult())
        {
            _log(
                $"World connection lost ({reason}). Ending MapServer so that it is restarted: a new process re-resolves the " +
                "World, leases a new actor-id block and registers again. Set " + EnvironmentVariableName + "=false to disable.");
        }
    }
}
