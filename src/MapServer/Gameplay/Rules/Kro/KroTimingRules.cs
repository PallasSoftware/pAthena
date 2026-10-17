using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Kro;

// kRO gameplay rules. Independently maintained: this class must not inherit from, delegate to or share a
// concrete algorithm with its counterpart in the other region. Behavior currently reproduces the pinned rAthena
// (e985006171d2eb320ee512a653f4c83aea3d81b6) Renewal source exactly; official kRO behavior is NOT verified.
//
// ASPD: status_base_amotion_pc RENEWAL_ASPD (status.cpp:2355-2399) with BaseASPD from the generated job_aspd.yml table; the
// amotion clamp is pc_maxaspd/2 .. MIN_ASPD/2 where pc_maxaspd = (2000 - max_aspd*10)*2 (battle_adjust_conf), i.e. a lower bound of 100
// for max_aspd 190 (fixture VF-17 cap cases). Walk speed: status_calc_speed (status.cpp:8018-8223). Player dmotion: status.cpp:4640-4645.
// Not modeled: shields, dual wield, DEX-branch weapons (Bow/Musical/Whip/firearms throw), ASPD potions/fixed-ASPD statuses,
// riding/mounts, slow statuses, third/summoner/extended max_aspd 193.
public sealed class KroTimingRules : ITimingRules
{
    private const int AmotionZeroAspd = 2000;
    private const int AmotionInterval = 10;
    private const int AmotionDividerPc = 2;
    private const int MaxAspd = 190; // conf/battle/player.conf max_aspd
    private const int MinAspd = 8000; // status.hpp MIN_ASPD

    private const int DefaultWalkSpeedMs = 150;
    private const int MinWalkSpeedMs = 20;
    private const int MaxWalkSpeedMs = 1000;

    public int AttackDelayMs(EffectiveCharacterStats attacker, WeaponType? weaponType, ushort jobClass)
    {
        var weapon = weaponType ?? WeaponType.Fist;
        if (weapon is WeaponType.Bow or WeaponType.Musical or WeaponType.Whip or WeaponType.Revolver or WeaponType.Rifle or WeaponType.Gatling or WeaponType.Shotgun or WeaponType.Grenade)
            throw new NotSupportedException($"ASPD for weapon type {weapon} uses the DEX formula branch, which is not modeled.");
        var baseAspd = GeneratedJobAspd.Base(jobClass, weapon);

        var dex = (double)attacker.Dexterity;
        var agi = (double)attacker.Agility;
        var tempAspd = Math.Sqrt(dex * dex / 5.0 + agi * agi * 0.5) * 0.25 + 196.0;

        var fixedAspdBonus = 0; // No fixed-ASPD status (Two-Hand Quicken/Berserk/etc) is modeled.
        var aspd = (int)(tempAspd + (fixedAspdBonus + 0) * agi / 200.0) - Math.Min(baseAspd, 200);

        var amotion = aspd;
        amotion += Math.Max(195 - amotion, 2) * (0 + attacker.AttackSpeedBonus) / 100;
        amotion = AmotionZeroAspd - amotion * AmotionInterval;
        amotion = Math.Clamp(amotion, AmotionZeroAspd - MaxAspd * AmotionInterval, MinAspd / AmotionDividerPc);
        return AmotionDividerPc * amotion;
    }

    // status->amotion is half the repeat delay (status->adelay = 2 * amotion for a PC).
    public uint AttackMotionMs(double attackDelayMs) => (uint)(attackDelayMs / AmotionDividerPc);

    public int WalkCellDurationMs(int moveSpeedHaste)
    {
        var speedRate = Math.Max(100 - moveSpeedHaste, 40);
        var speed = DefaultWalkSpeedMs * speedRate / 100;
        return Math.Clamp(speed, MinWalkSpeedMs, MaxWalkSpeedMs);
    }

    public int PlayerDamageMotionMs(int agility) => Math.Clamp(800 - agility * 4, 400, 800);
}
