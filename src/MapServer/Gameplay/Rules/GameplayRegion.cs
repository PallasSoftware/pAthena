namespace Athena.Net.MapServer.Gameplay.Rules;

// Which regional gameplay rule implementations a MapServer DEPLOYMENT runs ("gameplay_region" in map_athena.conf),
// selected once at the composition root. Not per player, per map or per rate group. Independent of both
// RagnarokRuleSet (Renewal / Pre-Renewal) and the client protocol (the exact client build / PACKETVER): selecting a
// region never implies packet framing or a supported client. Today only the official iRO client is supported; kRO
// gameplay rules exist so they can evolve independently, and are not verified against official kRO behavior.
public enum GameplayRegion
{
    Iro,
    Kro,
}
