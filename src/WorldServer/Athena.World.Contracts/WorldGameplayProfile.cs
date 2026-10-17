namespace Athena.Net.World.Contracts;

using Orleans;

// The gameplay profile a MapServer deployment runs (ruleset + regional rule implementations). Plain strings so this
// contract carries no MapServer enum and no gameplay logic - it only lets the shared Orleans world refuse to be served
// by MapServers that run different regional gameplay rules. Independent of the client protocol.
[GenerateSerializer]
public readonly record struct WorldGameplayProfile(
    [property: Id(0)] string Region,
    [property: Id(1)] string RuleSet);

// Accepted = the requested profile is (now) the world's profile. When false, Established is the profile that is already in
// force, which the caller must not contradict.
[GenerateSerializer]
public readonly record struct WorldGameplayProfileRegistration(
    [property: Id(0)] bool Accepted,
    [property: Id(1)] WorldGameplayProfile Established);

// One well-known instance per cluster. The first MapServer to register fixes the world's gameplay profile for the life of
// the World process; every later registration must match or is rejected, so one Orleans world can never silently mix
// incompatible regional gameplay configurations. The profile lives in this grain's memory and the grain refuses idle
// deactivation; a World restart restarts MapServer (WorldConnectionWatchdog), which registers afresh.
public interface IWorldGameplayProfileGrain : IGrainWithIntegerKey
{
    Task<WorldGameplayProfileRegistration> RegisterAsync(WorldGameplayProfile requested);
}

public static class WorldGameplayProfileGrainKey
{
    public const long WellKnownKey = 0;
}
