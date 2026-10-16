using System.Collections.Concurrent;
using System.Diagnostics;

namespace Athena.Net.MapServer.Net;

// Item 14 phase 4: a monster -> player attack is projected to this gateway's OWN sessions locally,
// the instant it resolves, and is then published to World so OTHER replicas can project it too. The
// World feed hands every entry to every gateway - including the one that published it - so without
// this registry the victim's own replica would write the same 0x08C8 a second time when it polls its
// own entry back.
//
// The executor registers the attack's ActionId BEFORE it publishes (so even a poll that races the
// publish RPC's return can recognise the echo), and MapTcpServer.FanOutEntryAsync consumes it when the
// matching MonsterAttackAction entry arrives. Bounded by construction: an id whose publish failed is
// forgotten immediately, and anything that is never consumed (for example the map has no sessions left
// to poll it) expires by age or, past the hard cap, by oldest-first eviction.
internal sealed class LocalMonsterAttackEchoes
{
    internal const int MaxEntries = 4096;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, long> _registeredAt = new();
    private readonly TimeProvider _time;

    public LocalMonsterAttackEchoes(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public int Count => _registeredAt.Count;

    public void Register(Guid actionId)
    {
        _registeredAt[actionId] = _time.GetTimestamp();
        if (_registeredAt.Count > MaxEntries) Prune();
    }

    // True exactly once per registered id: the caller must skip projecting that feed entry.
    public bool TryConsume(Guid actionId) => _registeredAt.TryRemove(actionId, out _);

    public void Forget(Guid actionId) => _registeredAt.TryRemove(actionId, out _);

    private void Prune()
    {
        var now = _time.GetTimestamp();
        foreach (var (id, at) in _registeredAt)
        {
            if (_time.GetElapsedTime(at, now) > MaxAge) _registeredAt.TryRemove(id, out _);
        }
        if (_registeredAt.Count <= MaxEntries) return;
        foreach (var (id, _) in _registeredAt.OrderBy(pair => pair.Value).Take(_registeredAt.Count - MaxEntries))
            _registeredAt.TryRemove(id, out _);
    }
}
