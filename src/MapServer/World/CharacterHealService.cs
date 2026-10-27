using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

public readonly record struct CharacterHealResult(CharacterGameplayState Before, CharacterGameplayState After)
{
    public bool HpChanged => Before.CurrentHp != After.CurrentHp;
    public bool SpChanged => Before.CurrentSp != After.CurrentSp;
}

// Generic `heal` runtime capability. Pinned legacy/rathena/src/map/script.cpp
// BUILDIN_FUNC(heal) calls status_heal(sd, hp, sp, 1), which clamps the resulting
// HP/SP to [0, MaxHp]/[0, MaxSp] (a heal past the maximum simply stops at the
// maximum; status_heal does not apply overflow elsewhere). This slice only supports
// non-negative heal amounts, matching every currently generated caller (e.g.
// Captain Carocc's `heal 9999,0`); status_heal's negative/damage path is out of scope.
// The heal is computed INSIDE the per-character mutation from the state that mutation sees, so a concurrent HP change (a monster
// hit landing between the caller's read and the write) is healed on top of, never overwritten. A dead character (HP 0) is not
// healed: status_heal returns before any change when `!status->hp` (status.cpp:1812-1813).
public sealed class CharacterHealService(CharacterGameplayStateSession stateSession)
{
    public async Task<CharacterHealResult?> HealAsync(int hp, int sp, CancellationToken cancellationToken)
    {
        if (hp < 0 || sp < 0) throw new ArgumentOutOfRangeException(nameof(hp), "This heal slice supports non-negative amounts only.");
        CharacterGameplayState? before = null;
        var persisted = await stateSession.MutateIfChangedAsync(current =>
        {
            before = current;
            if (current.CurrentHp == 0) return current;
            return current with
            {
                CurrentHp = (uint)Math.Min((long)current.CurrentHp + hp, current.MaxHp),
                CurrentSp = (uint)Math.Min((long)current.CurrentSp + sp, current.MaxSp),
            };
        }, cancellationToken, reason: "heal");
        return persisted is null ? null : new(before!, persisted);
    }
}

// The item-driven heal amounts of pinned rAthena, as pure functions of explicit inputs. Not regional: pinned rAthena has one code
// path for every server type (the only RENEWAL-guarded terms are statuses Athena cannot start yet, listed below).
public static class ItemHealFormula
{
    // Pinned enum sc_type value (status.hpp) of SC_INCHEALRATE, the only pc_itemheal modifier status an item script can start
    // (item_db_usable.yml); a test pins it against the constant the importer resolved from the real source.
    public const ushort IncHealRateStatusId = 267;

    // pc_itemheal (pc.cpp:10695-10786). bonus = 100 + 2*VIT (+2*INT for SP) + skill terms (10700, 10727), then SC_INCHEALRATE
    // (10719-10721), then `tmp = amount * bonus / 100; if (bonus != 100 && tmp > amount) amount = tmp` (10723-10725, 10743-10745).
    // vitality/intelligence are battle_status values (base + statuses). recoveryLevel = SM_RECOVERY, spRecoveryLevel =
    // MG_SRECOVERY, learningPotionLevel = AM_LEARNINGPOTION, incHealRate = SC_INCHEALRATE val1 (0 when inactive).
    // shortcut: terms with no Athena source yet contribute 0 - ranked-alchemist potions (potion_flag 2, needs the fame list),
    // bonus itemhealrate2/itemsphealrate2 and item-group/per-item heal rates (S5 bonus model), and the skill-only statuses
    // SC_CRITICALWOUND/DEATHHURT/NORECOVER_STATE/VITALITYACTIVATION/WATER_INSIGNIA/APPLEIDUN/EXTREMITYFIST/BITESCAR (F18);
    // add each when its source exists.
    public static (int Hp, int Sp) ItemHeal(int hp, int sp, int vitality, int intelligence, int recoveryLevel, int spRecoveryLevel, int learningPotionLevel, int incHealRate)
    {
        if (hp != 0)
        {
            var bonus = 100 + vitality * 2 + recoveryLevel * 10 + learningPotionLevel * 5;
            if (incHealRate != 0) bonus += bonus * incHealRate / 100;
            var scaled = hp * bonus / 100;
            if (bonus != 100 && scaled > hp) hp = scaled;
        }
        if (sp != 0)
        {
            var bonus = 100 + intelligence * 2 + spRecoveryLevel * 10 + learningPotionLevel * 5;
            var scaled = sp * bonus / 100;
            if (bonus != 100 && scaled > sp) sp = scaled;
        }
        return (hp, sp);
    }

    // status_percent_heal(bl, hp, sp) = status_percent_change(nullptr, bl, -hp, -sp, 0, 0) (status.hpp:3546-3548): a negated rate
    // below -99 heals the full maximum, otherwise apply_rate(max, rate) (status.cpp:1905-1920, common/utils.hpp:23); a non-zero
    // rate that rounds to 0 heals 1.
    public static (int Hp, int Sp) PercentHeal(uint maxHp, uint maxSp, int hpRate, int spRate) => (Rate(maxHp, hpRate), Rate(maxSp, spRate));

    private static int Rate(uint maximum, int rate)
    {
        if (rate <= 0) return 0;
        long value = rate > 99 ? maximum : maximum > 100000 ? maximum / 100 * rate : maximum * (long)rate / 100;
        return (int)Math.Min(Math.Max(value, 1), int.MaxValue);
    }
}
