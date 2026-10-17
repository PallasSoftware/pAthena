using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules.Iro;

// iRO gameplay rules. Independently maintained: this class must not inherit from, delegate to or share a
// concrete algorithm with its counterpart in the other region. Behavior currently reproduces the pinned rAthena
// (e985006171d2eb320ee512a653f4c83aea3d81b6) Renewal source exactly; official iRO behavior is NOT verified.
//
// Blessing: val2 = val1 for a PC (status.cpp:11567-11571); +val2 STR/INT/DEX (6776-6778, 6977-6979, 7059-7061) and +2*val1 HIT
// (7584-7585). Increase AGI: val2 = 2 + val1; +val2 AGI (6844-6845), +25 walk haste (8146-8152), +val1 ASPD bonus (8344-8346).
// Overlap: the default branch of status_change_start (status.cpp:10800-10804) - an unexpired HIGHER val1 makes a new start a no-op.
// Verified by fixtures VF-07/VF-08.
public sealed class IroStatusEffectRules : IStatusEffectRules
{
    public ActiveStatus CreateStatus(ushort statusId, int val1, int val2, int val3, DateTimeOffset expiresAt)
    {
        if (statusId == CharacterStatusEffectState.StatusIds.Blessing) val2 = val1;
        else if (statusId == CharacterStatusEffectState.StatusIds.IncreaseAgi) val2 = 2 + val1;
        return new ActiveStatus(statusId, val1, val2, val3, expiresAt);
    }

    public bool RetainsActiveOverNewStart(ActiveStatus active, DateTimeOffset now, int newVal1) => active.ExpiresAt > now && active.Val1 > newVal1;

    public EffectiveCharacterStats ApplyStatuses(PrimaryStats baseStats, Func<ushort, ActiveStatus?> lookup)
    {
        ushort strength = baseStats.Strength, agility = baseStats.Agility, vitality = baseStats.Vitality,
            intelligence = baseStats.Intelligence, dexterity = baseStats.Dexterity, luck = baseStats.Luck;
        var moveSpeedHaste = 0;
        var attackSpeedBonus = 0;
        var hitBonus = 0;

        if (lookup(CharacterStatusEffectState.StatusIds.Blessing) is { } blessing)
        {
            hitBonus = blessing.Val1 * 2;
            strength = (ushort)(strength + blessing.Val2);
            intelligence = (ushort)(intelligence + blessing.Val2);
            dexterity = (ushort)(dexterity + blessing.Val2);
        }

        if (lookup(CharacterStatusEffectState.StatusIds.IncreaseAgi) is { } increaseAgi)
        {
            agility = (ushort)(agility + increaseAgi.Val2);
            moveSpeedHaste = Math.Max(moveSpeedHaste, 25);
            attackSpeedBonus += increaseAgi.Val1;
        }

        return new EffectiveCharacterStats(strength, agility, vitality, intelligence, dexterity, luck, moveSpeedHaste, attackSpeedBonus, hitBonus);
    }
}
