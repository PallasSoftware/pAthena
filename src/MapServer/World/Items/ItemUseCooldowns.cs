namespace Athena.Net.MapServer.World.Items;

// Per-session, ephemeral item-use cooldown tracking (pinned pc_itemcd_check, pc.cpp:14766-14785). Unlike
// CharacterStatusEffectState (which models buffs with stat effects and type-specific overlap rules via
// IStatusEffectRules), an item delay group has none of that: it is purely "is this SC_ key's cooldown still
// running", keyed by ItemMetadata.DelayGroupKey (the item_db Delay.Status SC_ constant) - never persisted,
// never affecting EffectiveCharacterStats, so it is deliberately NOT folded into CharacterStatusEffectState.
public sealed class ItemUseCooldowns(TimeProvider timeProvider)
{
    private readonly Dictionary<int, DateTimeOffset> _expiresAt = [];
    private readonly Lock _gate = new();

    // Null when the key has no active cooldown; otherwise the remaining time, matching pc_itemcd_check's
    // DIFF_TICK(timer->tick, tick) reply value (clif_msg_value MSI_ITEM_REUSE_LIMIT_SECOND).
    public TimeSpan? Remaining(int delayGroupKey)
    {
        lock (_gate)
        {
            if (_expiresAt.TryGetValue(delayGroupKey, out var expiresAt))
            {
                var remaining = expiresAt - timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero) return remaining;
                _expiresAt.Remove(delayGroupKey);
            }
            return null;
        }
    }

    // Arms (or re-arms) the cooldown. Mirrors pc_itemcd_check's sc_start call: always overwrites with the
    // new duration - there is no accumulation/stacking of item delays.
    public void Start(int delayGroupKey, TimeSpan duration)
    {
        lock (_gate) { _expiresAt[delayGroupKey] = timeProvider.GetUtcNow() + duration; }
    }
}
