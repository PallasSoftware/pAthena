using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Startup;

/// <summary>
/// Fail-closed re-verification of this MapServer's gameplay profile against the World after a reconnect (the World may have
/// restarted with different configuration while this process kept running). Any mismatch - or failure to verify at all - ends
/// MapServer through <paramref name="onIncompatible"/>; an unverifiable World is never served from.
/// </summary>
public sealed class WorldGameplayProfileMonitor(
    Func<WorldGameplayProfile, Task<WorldGameplayProfileRegistration>> register,
    GameplayOptions options,
    Action<string> log,
    Action onIncompatible)
{
    /// <summary>Process exit code when MapServer ended because the World runs a different gameplay profile (EX_CONFIG).</summary>
    public const int ExitCode = 78;

    private int _failed;

    public bool Failed => Volatile.Read(ref _failed) != 0;

    public async Task VerifyAfterReconnectAsync()
    {
        try
        {
            await WorldGameplayProfileGuard.EnsureCompatibleAsync(register, options);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                log($"World gameplay profile could not be verified after reconnect ({ex.GetType().Name}: {ex.Message}). Ending MapServer (fail closed).");
                onIncompatible();
            }
        }
    }
}
