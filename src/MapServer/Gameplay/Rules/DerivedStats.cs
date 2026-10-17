namespace Athena.Net.MapServer.Gameplay.Rules;

// Immutable derived-stat result of IStatRules.CalculateDerivedStats (neutral model, shared by every region).
public readonly record struct DerivedStats(int BaseAttack, int Hit, int Flee, int PerfectFlee, int Critical, int SoftDefense, int SoftMagicDefense, int HardDefense = 0, int MagicAttackMin = 0, int MagicAttackMax = 0);
