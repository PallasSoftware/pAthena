using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Regional gameplay rules for the temporary statuses Athena models (Blessing, Increase AGI). Storage, locking and
// expiry scheduling stay in CharacterStatusEffectState; only the gameplay decisions live here.
public interface IStatusEffectRules
{
    // Builds the stored entry for a start request, deriving val2 the way status_change_start does.
    ActiveStatus CreateStatus(ushort statusId, int val1, int val2, int val3, DateTimeOffset expiresAt);

    // True when a start with newVal1 must be a no-op because an unexpired stronger entry is active.
    bool RetainsActiveOverNewStart(ActiveStatus active, DateTimeOffset now, int newVal1);

    // Effective stats/haste/ASPD/HIT bonus from the base stats and the active statuses (lookup returns null when inactive).
    EffectiveCharacterStats ApplyStatuses(PrimaryStats baseStats, Func<ushort, ActiveStatus?> lookup);
}
