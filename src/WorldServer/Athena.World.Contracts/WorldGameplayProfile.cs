namespace Athena.Net.World.Contracts;

using Orleans;

// The gameplay profile a MapServer deployment runs (ruleset + regional rule implementations). Plain strings so this
// contract carries no MapServer enum and no gameplay logic - it only lets the shared Orleans world refuse to be served
// by MapServers that run different regional gameplay rules. Independent of the client protocol.
[GenerateSerializer]
public readonly record struct WorldGameplayProfile(
    [property: Id(0)] string Region,
    [property: Id(1)] string RuleSet);

// Accepted = the requested profile equals the World's configured profile. Established is always the World's configured
// profile (what the caller must match), whether or not the request was accepted.
[GenerateSerializer]
public readonly record struct WorldGameplayProfileRegistration(
    [property: Id(0)] bool Accepted,
    [property: Id(1)] WorldGameplayProfile Established);

// One well-known instance per cluster. The World owns an immutable, deployment-configured profile (never learned from a
// MapServer): the grain answers from the configuration of the silo hosting it, so a re-created activation or a failover to another
// silo recovers the same profile with no persisted state, and every silo verifies at startup that it agrees with the cluster.
// MapServers register their profile at startup and again on every Orleans reconnect, and stop serving on a mismatch.
public interface IWorldGameplayProfileGrain : IGrainWithIntegerKey
{
    Task<WorldGameplayProfileRegistration> RegisterAsync(WorldGameplayProfile requested);
}

public static class WorldGameplayProfileGrainKey
{
    public const long WellKnownKey = 0;
}
