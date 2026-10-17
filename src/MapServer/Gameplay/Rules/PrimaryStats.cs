namespace Athena.Net.MapServer.Gameplay.Rules;

// Immutable six-stat value used as calculation input/output by the regional stat and status-effect rules.
public readonly record struct PrimaryStats(ushort Strength, ushort Agility, ushort Vitality, ushort Intelligence, ushort Dexterity, ushort Luck);
