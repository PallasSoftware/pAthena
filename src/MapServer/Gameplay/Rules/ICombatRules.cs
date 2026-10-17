using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Regional basic (no-skill) melee damage. Each region owns an independent concrete implementation; consumers
// (MonsterCombatCoordinator, monster attack cadence) depend only on this interface and never ask which region is active.
public interface ICombatRules
{
    // Player -> monster basic attack (weapon damage including the size fix, then monster DEF).
    BasicAttackDamageResult CalculatePlayerBasicAttack(BasicAttackContext context);

    // Monster -> player basic attack (mob ATK band + BATK, then the target's hard/soft DEF from the combat snapshot).
    BasicAttackDamageResult CalculateMonsterBasicAttack(MobDefinition attacker, PlayerCombatSnapshot target);
}
