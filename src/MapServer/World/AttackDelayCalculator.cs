namespace Athena.Net.MapServer.World;

// Traced from pinned rAthena's RENEWAL_ASPD player attack-delay path (status.cpp), for the ONLY
// subset this codebase currently models: a fresh Novice with no skills/riding/shield/dual-wield
// and no active status beyond Increase AGI's flat ASPD bonus (already computed as
// EffectiveCharacterStats.AttackSpeedBonus - see CharacterStatusEffectState's own doc comment).
// Not a general ASPD/status subsystem - this is the smallest source-backed calculation that
// produces status->adelay (the real interval unit_set_attackdelay/unit_attack_timer_sub schedule
// the next repeat-attack tick with) for this exact slice.
//
// Full traced chain (status_base_amotion_pc, status.cpp:2355-2399, RENEWAL_ASPD branch, called
// from the RE PC branch of status_calc_bl_ at status.cpp:6389-6417):
//   aspd = job->aspd_base[weapon]                                   (db/re/job_aspd.yml)
//   temp_aspd = sqrt(DEX^2/5 + AGI^2*0.5) * 0.25 + 196               (non-DEX-flagged weapon type;
//                                                                     Dagger/Fist both use this
//                                                                     branch, not the Bow/etc one)
//   val = 0 (no SA_ADVANCEDBOOK/SG_DEVIL/GS_SINGLEACTION skills, no riding - all unmodeled/false
//         for a fresh Novice)
//   aspd = (int)(temp_aspd + (status_calc_aspd(fixed=true, always 0 - no active fixed-ASPD
//         status like Two-Hand Quicken/Berserk is modeled) + val) * AGI / 200) - min(aspd, 200)
// Then status_calc_bl_'s own RE PC continuation (status.cpp:6392-6417):
//   amotion = aspd (renamed, same variable reused in pinned source)
//   amotion += (max(195 - amotion, 2) * (aspd_rate2(0, unmodeled) + status_calc_aspd(fixed=false)))
//              / 100 - the non-fixed term is EXACTLY AttackSpeedBonus for this codebase's
//              modeled statuses (status.cpp:8345-8346, SC_INCREASEAGI val1, inside the `else`/
//              non-fixed branch of status_calc_aspd - the only bonus source
//              CharacterStatusEffectState.Recalculate currently produces)
//   amotion = AMOTION_ZERO_ASPD(2000) - amotion * AMOTION_INTERVAL(10)      // ASPD -> amotion
//   amotion += bAspd_add (0, no unmodeled aspd_add item bonus)
//   amotion = status_calc_fix_aspd(amotion)  // no-op: no SC_OVERED_BOOST/GUST_OPTION/etc modeled
//   status->amotion = clamp(amotion, pc_maxaspd/2, MIN_ASPD/2)     // conf/battle/player.conf
//                                                                   max_aspd=190 -> clamp(_,95,4000)
//   status->adelay = AMOTION_DIVIDER_PC(2) * status->amotion       // unit_set_attackdelay's
//                                                                   DELAY_EVENT_ATTACK input
//
// Deliberately NOT modeled (would need broader status/skill/equipment state): shields, dual-wield,
// riding, weapon-mastery/ASPD skills, DEX-flagged ranged weapon types (Bow/Musical/Whip/firearms
// use a different temp_aspd formula), any fixed-ASPD status (Two-Hand Quicken, Berserk, ASPD
// potions), aspd_rate/aspd_rate2 item or equipment-slot bonuses, bAspd_add.
public static class AttackDelayCalculator
{
    private const int AmotionZeroAspd = 2000;
    private const int AmotionInterval = 10;
    private const int AmotionDividerPc = 2;
    private const int MaxAspd = 190; // conf/battle/player.conf max_aspd (third/summoner/extended classes use 193: unmodeled, see below)
    private const int MinAspd = 8000; // status.hpp MIN_ASPD

    // job_aspd.yml BaseASPD (Renewal units) for the job and weapon via the generated table; a bare hand is Fist. Only
    // weapon types that use the non-DEX temp_aspd branch (status.cpp:2380-2392) are modeled: Bow, Musical, Whip and the
    // firearms use the DEX branch and throw until they have their own reference fixtures (VF-17 records them).
    public static int BaseAspdForWeapon(ushort jobClass, WeaponType? weaponType)
    {
        var weapon = weaponType ?? WeaponType.Fist;
        if (weapon is WeaponType.Bow or WeaponType.Musical or WeaponType.Whip or WeaponType.Revolver or WeaponType.Rifle or WeaponType.Gatling or WeaponType.Shotgun or WeaponType.Grenade)
            throw new NotSupportedException($"ASPD for weapon type {weapon} uses the DEX formula branch, which is not modeled.");
        return Generated.Progression.GeneratedJobAspd.Base(jobClass, weapon);
    }

    // attack motion (status->amotion) is half the repeat delay (status->adelay = 2 * amotion for a PC); the
    // client plays this as the swing animation length (sdelay in clif_damage). Derived from the delay already
    // frozen in the logical attack's snapshot so retries never re-read newer stats.
    public static uint AttackMotionMs(double attackDelayMs) => (uint)(attackDelayMs / AmotionDividerPc);

    // Returns the milliseconds between authoritative repeat-attack hits (pinned status->adelay).
    public static int AttackDelayMs(EffectiveCharacterStats attacker, WeaponType? weaponType, ushort jobClass)
    {
        var baseAspd = BaseAspdForWeapon(jobClass, weaponType);
        var dex = (double)attacker.Dexterity;
        var agi = (double)attacker.Agility;
        var tempAspd = Math.Sqrt(dex * dex / 5.0 + agi * agi * 0.5) * 0.25 + 196.0;

        var fixedAspdBonus = 0; // No fixed-ASPD status (Two-Hand Quicken/Berserk/etc) is modeled.
        var aspd = (int)(tempAspd + (fixedAspdBonus + 0) * agi / 200.0) - Math.Min(baseAspd, 200);

        var amotion = aspd;
        amotion += Math.Max(195 - amotion, 2) * (0 + attacker.AttackSpeedBonus) / 100;
        amotion = AmotionZeroAspd - amotion * AmotionInterval;

        // pc_maxaspd = (2000 - max_aspd*10) * 2 (battle_adjust_conf), clamped as pc_maxaspd/2 .. MIN_ASPD/2 (status.cpp:6415):
        // a lower amotion bound of 100 for max_aspd 190 (fixture VF-17 cap cases), not "190/2".
        amotion = Math.Clamp(amotion, AmotionZeroAspd - MaxAspd * AmotionInterval, MinAspd / AmotionDividerPc);
        return AmotionDividerPc * amotion;
    }
}
