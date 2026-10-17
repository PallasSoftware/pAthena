using Athena.Net.MapServer.Generated.GameData.SizeFix;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Kro;

// kRO gameplay rules. Independently maintained: this class must not inherit from, delegate to or share a
// concrete algorithm with its counterpart in the other region. Behavior currently reproduces the pinned rAthena
// (e985006171d2eb320ee512a653f4c83aea3d81b6) Renewal source exactly; official kRO behavior is NOT verified.
//
// Player -> monster (battle_calc_attack -> battle_calc_damage_parts, battle.cpp:3911/5525, battle_calc_base_weapon_attack
// battle.cpp:2440-2490, battle_calc_sizefix battle.cpp:2429, battle_calc_defense_reduction battle.cpp:4720-4884):
//   damage = (2*BATK + roll[watk -/+ variance + STR bonus] * sizeFix/100) * (4000+DEF)/(4000+10*DEF) - softDEF(mob) ; < 1 is a miss.
// Monster -> player (battle_calc_base_damage battle.cpp:2515-2571, status_base_atk_min/max status.cpp:2522-2554):
//   damage = roll[atkmin, atkmax) + (STR + level) ; then the same defense reduction with the player's hard/soft DEF.
// Verified by fixtures VF-06 (player) and VF-14/VF-15 (monster). Accuracy/FLEE, crit, elements, cards, skills are unmodeled.
public sealed class KroCombatRules(Func<int, int, int>? rollWeaponAtk = null, Func<int, int, int>? rollMonsterAtk = null) : ICombatRules
{
    public BasicAttackDamageResult CalculatePlayerBasicAttack(BasicAttackContext context)
    {
        var attacker = context.Attacker;
        var weapon = context.EquippedWeapon;
        var target = context.Target;
        var roll = rollWeaponAtk ?? ((min, max) => min >= max ? min : Random.Shared.Next(min, max + 1));

        var batk = BaseAttack(attacker, context.AttackerBaseLevel);
        var statusAtk = 2L * batk; // battle_calc_damage_parts: wd->statusAtk *= 2 (battle.cpp:3911).

        long weaponAtk = 0;
        if (weapon is not null)
        {
            var watk = weapon.Attack; // status_calc_watk no-op: no active watk-modifying status.
            var variance = 5.0 * watk * weapon.WeaponLevel / 100.0;
            var baseStatBonus = watk * attacker.Strength / 200.0;
            var atkmin = Math.Max(0, (int)(watk - variance + baseStatBonus));
            var atkmax = Math.Min(ushort.MaxValue, (int)(watk + variance + baseStatBonus));
            if (atkmin > atkmax) atkmin = atkmax; // Defensive: pinned code never violates this for real data.

            weaponAtk = roll(atkmin, atkmax);
            weaponAtk = weaponAtk * GeneratedSizeFix.Percent(weapon.WeaponType, target.Size) / 100;
        }

        long damage = statusAtk + weaponAtk; // equipAtk=0, percentAtk=0, masteryAtk=0, patk=0.

        var def1 = target.Defense;
        var def2 = (target.Level + target.Vit) / 2;
        damage = damage * (4000 + def1) / (4000 + 10L * def1) - def2;

        return damage < 1 ? new BasicAttackDamageResult(0, IsMiss: true) : new BasicAttackDamageResult((uint)damage, IsMiss: false);
    }

    public BasicAttackDamageResult CalculateMonsterBasicAttack(MobDefinition attacker, PlayerCombatSnapshot target)
    {
        // The roll keeps its inclusive [min, max] contract; this hands it the reference's exclusive upper bound.
        var roll = rollMonsterAtk ?? ((min, max) => min >= max ? min : Random.Shared.Next(min, max + 1));

        var rhwAtk = attacker.Attack;
        var atkmin = rhwAtk * 80 / 100;
        var atkmax = rhwAtk * 120 / 100;
        if (atkmin > atkmax) atkmin = atkmax;

        // `(atkmax>atkmin ? rnd()%(atkmax-atkmin) : 0) + atkmin` -> [atkmin, atkmax) exclusive, plus status->batk,
        // which for BL_MOB is status_base_atk = str + level (status.cpp:2488-2491).
        long damage = (atkmax > atkmin ? roll(atkmin, atkmax - 1) : atkmin) + (attacker.Str + attacker.Level);

        var def1 = target.HardDefense; // equipped-armor hard DEF (fixture VF-15)
        var def2 = target.SoftDefense; // status def2 from the combat snapshot (IStatRules)
        damage = damage * (4000 + def1) / (4000 + 10L * def1) - def2;

        return damage < 1 ? new BasicAttackDamageResult(0, IsMiss: true) : new BasicAttackDamageResult((uint)damage, IsMiss: false);
    }

    // status_base_atk, RENEWAL BL_PC non-ranged weapon, POW = 0 (status.cpp:2475-2477); clamp 0..USHRT_MAX.
    private static int BaseAttack(EffectiveCharacterStats attacker, ushort baseLevel)
    {
        const int pow = 0; // Fresh-character default.
        var str = (attacker.Strength * 10 + attacker.Dexterity * 10 / 5 + attacker.Luck * 10 / 3 + baseLevel * 10 / 4) / 10 + 5 * pow;
        return Math.Clamp(str, 0, ushort.MaxValue);
    }
}
