using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Regional timing rules: attack delay/motion (ASPD), walking cell duration and player damage motion. Attack motion,
// attack delay and damage motion are distinct values and stay distinct here.
public interface ITimingRules
{
    // status->adelay for a player (twice the attack motion).
    int AttackDelayMs(EffectiveCharacterStats attacker, WeaponType? weaponType, ushort jobClass);

    // status->amotion derived from a frozen attack delay (the swing animation length sent to clients).
    uint AttackMotionMs(double attackDelayMs);

    int WalkCellDurationMs(int moveSpeedHaste);

    // Player dmotion (the flinch length when a monster hits the player).
    int PlayerDamageMotionMs(int agility);
}
