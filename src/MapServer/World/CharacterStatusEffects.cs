using Athena.Net.MapServer.Gameplay.Rules;

namespace Athena.Net.MapServer.World;

// Generic temporary-status foundation. Deliberately not the complete Ragnarok status
// system: it models "a status ID with up to three integer values and an expiration
// instant", matches pinned rAthena sc_start(type, tick, val1[, val2, val3, val4])
// semantics closely enough for Blessing/Increase AGI, and is designed to grow to more
// statuses later without a rewrite. Temporary statuses live only in MapServer runtime
// memory (never persisted to CharacterGameplayState) and use TimeProvider so tests can
// control expiration deterministically without real timers or one Task.Delay per status.
public readonly record struct ActiveStatus(ushort StatusId, int Val1, int Val2, int Val3, DateTimeOffset ExpiresAt);

// Effective (base + active statuses) character stats. Derived on demand from base
// persisted stats plus currently active (non-expired) statuses; never itself persisted.
public readonly record struct EffectiveCharacterStats(
    ushort Strength,
    ushort Agility,
    ushort Vitality,
    ushort Intelligence,
    ushort Dexterity,
    ushort Luck,
    int MoveSpeedHaste,
    int AttackSpeedBonus,
    int HitBonus = 0);

// Per-session mutable status state. Each MapClientSession owns its own instance, so
// concurrent sessions never share mutable status state - but WITHIN one session,
// MapClientSession's background RunStatusExpirationLoopAsync (reading NextExpiration/
// ExpireDue) races against generated NPC scripts calling Start (via INpcScriptHost) on
// other async paths of the SAME session. Dictionary<> is not safe for concurrent
// readers/writers, so every _statuses access below is synchronized through one private
// gate. Lock bodies are kept synchronous and small (no await inside the lock); operations
// that must observe one coherent snapshot (e.g. Blessing + IncreaseAgi both read for the
// same Recalculate call, or "identify due statuses" + "remove those exact statuses" in
// ExpireDue) take the lock once for the whole operation rather than once per sub-step.
// Immutable result of CharacterStatusEffectState.Capture: the exact gameplay state the effective stats were derived
// from, so consumers never re-read State for a second value (level, HP, ...) after taking the stats.
public sealed record CharacterStatusSnapshot(CharacterGameplayState State, EffectiveCharacterStats Effective, DateTimeOffset CapturedAt);

public enum StatusStartOutcome { Started, Replaced, Retained }

public sealed class CharacterStatusEffectState(TimeProvider timeProvider, IStatusEffectRules statusRules, IStatRules statRules)
{
    private readonly Dictionary<ushort, ActiveStatus> _statuses = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider;

    public static class StatusIds
    {
        // Pinned legacy/rathena/src/map/status.hpp enum sc_type (SC_STONE = 0 origin).
        public const ushort Blessing = 30;
        public const ushort IncreaseAgi = 32;
    }

    // Pinned rAthena sc_start(type, tick, val1) re-applies an active status by
    // overwriting its stored values and duration outright (status_change_start does
    // not stack/extend on top of an existing instance for these status types); it does
    // not average or add to the previous instance. Matches that: the newest call wins.
    //
    // val2/val3 default to 0, matching sc_start's own 3-argument default (no val2/val3
    // supplied) - both the script BUILDIN_FUNC(sc_start) 3-arg form (script.cpp:12450-12453,
    // start_type==1: status_change_start(bl, bl, type, rate, val1, 0, 0, val4, tick, flag))
    // and the real skill-cast path used by AL_INCAGI (SkillIncreaseAgi::castendNoDamageId in
    // skills/acolyte/incagi.cpp:28, via the sc_start(...) C++ helper at status.hpp:3665-3667,
    // which is likewise `status_change_start(..., val1, val2=0, val3=0, val4=0, ...)`) pass
    // val2=0 to status_change_start. Both then converge on the SAME function,
    // status_change_start_post_delay (status.cpp:10309-13368, entered unconditionally for
    // delay<=0 at status.cpp:10269-10270), whose "val settings" switch at status.cpp:10812
    // (guarded only by `if(!(flag&SCSTART_LOADED))`, which is not set by either call path)
    // computes type-specific defaults before storing them:
    //   - SC_BLESSING: status.cpp:11566-11571, val2 = val1 for a BL_PC target.
    //   - SC_INCREASEAGI: status.cpp:10844-10854, val2 = 2 + val1 ("// Agi change" comment).
    // Both statuses are therefore identical between the script-command and skill-cast paths;
    // this is a property of status_change_start_post_delay, not something either caller
    // computes, so it is applied unconditionally here rather than duplicated at call sites.
    //
    // Overlap rule (status.cpp:10800-10804, default branch, for statuses without the
    // OverlapIgnoreLevel flag - neither Blessing nor Increase AGI has it, db/re/status.yml): an
    // active entry with a HIGHER val1 makes the new start a no-op that still returns true; the
    // existing val1/val2/deadline are untouched. Equal or lower active val1 is replaced and
    // re-armed with the new duration. Verified by pinned-runner fixtures VF-07/VF-08.
    public StatusStartOutcome Start(ushort statusId, int durationMilliseconds, int val1, int val2 = 0, int val3 = 0)
    {
        if (durationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds), "Status duration must be positive.");
        var expiresAt = _timeProvider.GetUtcNow().AddMilliseconds(durationMilliseconds);
        var status = statusRules.CreateStatus(statusId, val1, val2, val3, expiresAt);
        lock (_gate)
        {
            if (_statuses.TryGetValue(statusId, out var active))
            {
                if (statusRules.RetainsActiveOverNewStart(active, _timeProvider.GetUtcNow(), val1)) return StatusStartOutcome.Retained;
                _statuses[statusId] = status;
                return StatusStartOutcome.Replaced;
            }
            _statuses[statusId] = status;
            return StatusStartOutcome.Started;
        }
    }

    // status_change_end for an active status (script sc_end): the entry becomes due NOW, so the session's expiration driver
    // removes it through ExpireDue and sends exactly the end packet / stat resync a natural expiry sends - one end path, not two.
    // False when the status is not active (sc_end on an absent status is a no-op, script.cpp:12495-12496).
    public bool End(ushort statusId)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_statuses.TryGetValue(statusId, out var status) || status.ExpiresAt <= now) return false;
            _statuses[statusId] = status with { ExpiresAt = now };
            return true;
        }
    }

    // Treats an already-expired status as inactive without removing it. Reads (TryGet,
    // Recalculate, effective-stat sync) must reflect expiration immediately, but must not
    // themselves destroy the ActiveStatus record - the expiration DRIVER (MapClientSession's
    // scheduler) needs to observe and act on the transition via ExpireDue below (send
    // 0x0196/0x0141) before the entry disappears. Only ExpireDue removes entries.
    public bool TryGet(ushort statusId, out ActiveStatus status)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_statuses.TryGetValue(statusId, out status) && status.ExpiresAt > now) return true;
        }
        status = default;
        return false;
    }

    // Removes and returns every status whose expiration is at or before `now`. This is the
    // ONLY member that mutates _statuses by removal - it exists so the expiration driver can
    // explicitly observe which statuses just transitioned to expired (to synchronize the
    // client) rather than having them silently vanish. Already-expired-but-not-yet-`ExpireDue`d
    // entries are still returned by this dictionary's iteration (TryGet/Recalculate just treat
    // them as inactive), so nothing is lost between "became due" and "driver processed it".
    // Identifying due statuses and removing them is one atomic critical section - never split
    // across two lock acquisitions, or a concurrent Start could re-add a status between the
    // "identify" and "remove" steps and have its fresh entry incorrectly removed.
    public IReadOnlyList<ActiveStatus> ExpireDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            List<ActiveStatus>? due = null;
            foreach (var (id, status) in _statuses)
            {
                if (status.ExpiresAt > now) continue;
                (due ??= []).Add(status);
            }
            if (due is null) return [];
            foreach (var status in due) _statuses.Remove(status.StatusId);
            return due;
        }
    }

    // Earliest deadline among currently-stored statuses (whether or not already expired -
    // an already-due one sorts first, which is exactly what the scheduler needs: "wake now").
    // Null when nothing is stored, meaning the scheduler should wait indefinitely.
    public DateTimeOffset? NextExpiration
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? next = null;
                foreach (var status in _statuses.Values)
                    if (next is null || status.ExpiresAt < next) next = status.ExpiresAt;
                return next;
            }
        }
    }

    public IReadOnlyCollection<ActiveStatus> ActiveStatuses
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            lock (_gate)
            {
                return _statuses.Values.Where(status => status.ExpiresAt > now).ToArray();
            }
        }
    }

    // Derives effective stats from persisted base stats plus every currently active
    // (non-expired) status. Pinned legacy/rathena/src/map/status.cpp semantics (see Start's
    // remarks for full status_change_start_post_delay tracing):
    //   - SC_BLESSING: status_calc_str/int/dex (status.cpp:6776-6778, 6977-6979, 7059-7061)
    //     add +val2 (== val1 for a PC target) to STR, INT, and DEX.
    //   - SC_INCREASEAGI: status_calc_agi (status.cpp:6843-6844) adds +val2 (== 2 + val1) to
    //     AGI - it DOES modify the AGI stat, unlike an earlier incorrect assumption in this
    //     file. It additionally grants a flat +25 move-speed haste value (status_calc_speed,
    //     status.cpp:8151-8152) and a +val1 attack-speed bonus (status_calc_aspd,
    //     status.cpp:8344-8345); both are unconditional (no RENEWAL/#ifdef guards around
    //     either SC_INCREASEAGI line in this pinned revision).
    //
    // Reads Blessing and IncreaseAgi from ONE coherent snapshot taken under a single lock
    // acquisition (SnapshotForRecalculate), rather than two independent TryGet calls each
    // taking/releasing the gate - otherwise a Start racing between the two lookups could let
    // one stat family observe an older dictionary state than the other.
    public EffectiveCharacterStats Recalculate(CharacterGameplayState baseState)
    {
        var now = _timeProvider.GetUtcNow();
        var snapshot = SnapshotForRecalculate();
        return RecalculateFrom(baseState, id => snapshot.TryGetValue(id, out var status) && status.ExpiresAt > now ? status : null);
    }

    // One mutually consistent (gameplay state, effective stats) pair. The two inputs have different owners
    // (CharacterGameplayStateSession publishes immutable states after persistence; this class owns the status
    // dictionary under its own leaf gate), so a plain "read state, then read statuses" can mix instants.
    // Lock-free validate-and-retry: read the state, copy the statuses under the gate (the only lock taken, never
    // while holding another), and accept only if the state reference is unchanged afterwards. Gameplay states are
    // versioned and never revisited, so an unchanged reference means the state was current throughout the
    // status copy - the pair is a real instant. No lock ordering exists to deadlock on.
    public CharacterStatusSnapshot Capture(Func<CharacterGameplayState> readState)
    {
        while (true)
        {
            var state = readState();
            var now = _timeProvider.GetUtcNow();
            var statuses = SnapshotForRecalculate();
            if (!ReferenceEquals(state, readState())) continue;
            var effective = RecalculateFrom(state, id => statuses.TryGetValue(id, out var status) && status.ExpiresAt > now ? status : null);
            return new CharacterStatusSnapshot(state, effective, now);
        }
    }

    // Effective stats as of just BEFORE an ExpireDue(now) removal, for the expiration driver's
    // "before" snapshot. Distinct from Recalculate(baseState) because by the time
    // ProcessDueStatusExpirationsAsync runs, the clock has already reached the statuses' own
    // ExpiresAt (that is what woke the scheduler), so a time-gated TryGet would already treat
    // them as inactive and produce a false "nothing changed" diff. This reads the raw stored
    // entries (still present - ExpireDue has not run yet) unconditionally instead, from the
    // same kind of single coherent snapshot Recalculate uses.
    public EffectiveCharacterStats RecalculateBeforeExpiration(CharacterGameplayState baseState)
    {
        var snapshot = SnapshotForRecalculate();
        return RecalculateFrom(baseState, id => snapshot.TryGetValue(id, out var status) ? status : null);
    }

    private Dictionary<ushort, ActiveStatus> SnapshotForRecalculate()
    {
        lock (_gate) { return new Dictionary<ushort, ActiveStatus>(_statuses); }
    }

    // Gameplay (which stats a status changes, by how much) lives in the injected regional rules; this class owns storage,
    // locking and the consistent snapshot only.
    private EffectiveCharacterStats RecalculateFrom(CharacterGameplayState baseState, Func<ushort, ActiveStatus?> lookup) =>
        statusRules.ApplyStatuses(statRules.BasePrimaryStats(baseState), lookup);
}
