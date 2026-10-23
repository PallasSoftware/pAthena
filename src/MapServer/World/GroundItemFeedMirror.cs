using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

public readonly record struct GroundItemVisibilityChange(uint GroundItemId, WorldGroundItemView? Appeared)
{
    public bool IsRemoval => Appeared is null;
}

// One MapServer replica's read-only mirror of one map's ground items, fed only by World's ground-item feed
// (IWorldRuntime.PollGroundItemFeedAsync). World stays the single authority: this never creates, claims or removes an
// item on its own, and a pickup still goes through World's exclusive claim. It exists so sessions on EVERY replica see
// the same items, whichever replica's monster dropped them.
//
// Stale events cannot resurrect an item: entries at or below the committed cursor are skipped (duplicates and replays),
// a snapshot older than the cursor in the same epoch is ignored, and World never reuses a GroundItemId within an epoch,
// so its Removed entry always follows its Appeared entry. A new epoch (World restart) or a resync snapshot replaces the
// mirror by diff, reporting every vanished item as removed.
public sealed class GroundItemFeedMirror
{
    private readonly Dictionary<uint, WorldGroundItemView> _items = [];

    public WorldGroundItemFeedCursor? Cursor { get; private set; }
    public IReadOnlyCollection<WorldGroundItemView> Items => _items.Values;

    public IReadOnlyList<GroundItemVisibilityChange> Apply(WorldGroundItemFeedPage page)
    {
        var changes = new List<GroundItemVisibilityChange>();
        if (page.Snapshot is { } snapshot)
        {
            if (Cursor is { } current && current.Epoch.Equals(page.Epoch) && page.AsOfSequence < current.Sequence) return changes; // stale
            var present = snapshot.ToDictionary(view => view.GroundItemId);
            foreach (var id in _items.Keys.Where(id => !present.ContainsKey(id)).ToArray())
            {
                _items.Remove(id);
                changes.Add(new(id, null));
            }
            foreach (var view in snapshot)
                if (_items.TryAdd(view.GroundItemId, view)) changes.Add(new(view.GroundItemId, view));
            Cursor = new(page.Epoch, page.AsOfSequence);
            return changes;
        }
        if (page.ResyncRequired || Cursor is not { } cursor || !cursor.Epoch.Equals(page.Epoch)) return changes;

        foreach (var entry in page.Entries ?? [])
        {
            if (entry.Sequence <= Cursor!.Value.Sequence) continue;
            if (entry.Kind == WorldGroundItemFeedEntryKind.Appeared && entry.Item is { } item)
            {
                if (_items.TryAdd(entry.GroundItemId, item)) changes.Add(new(entry.GroundItemId, item));
            }
            else if (entry.Kind == WorldGroundItemFeedEntryKind.Removed && _items.Remove(entry.GroundItemId))
            {
                changes.Add(new(entry.GroundItemId, null));
            }
            Cursor = new(page.Epoch, entry.Sequence);
        }
        Cursor = new(page.Epoch, Math.Max(Cursor!.Value.Sequence, page.AsOfSequence));
        return changes;
    }
}

// Which mirrored ground items one session can currently see: pinned map_foreachinallarea over AREA_SIZE (a square
// |dx| <= area, |dy| <= area, battle_config.area_size), re-evaluated when the player moves or the mirror changes - an item
// entering that square is what pinned sends clif_getareachar_item for, one leaving it (or picked up / expired) what it
// sends clif_clearflooritem for. Server-side only: the 0x0ADD appearance packet still has unresolved fields, so nothing
// is projected to the client yet (Protocol-pending-capture).
public sealed class GroundItemSessionVisibility
{
    private readonly Dictionary<uint, WorldGroundItemView> _visible = [];

    public IReadOnlyCollection<uint> VisibleIds => _visible.Keys;

    public (IReadOnlyList<WorldGroundItemView> Entered, IReadOnlyList<uint> Left) Reconcile(
        IReadOnlyCollection<WorldGroundItemView> mapItems, ushort x, ushort y, ushort areaSize = WorldVisibilityOptions.DefaultAreaSize)
    {
        var inRange = mapItems.Where(item => Math.Abs(item.X - x) <= areaSize && Math.Abs(item.Y - y) <= areaSize).ToDictionary(item => item.GroundItemId);
        var left = _visible.Keys.Where(id => !inRange.ContainsKey(id)).ToArray();
        foreach (var id in left) _visible.Remove(id);
        var entered = inRange.Values.Where(item => _visible.TryAdd(item.GroundItemId, item)).ToArray();
        return (entered, left);
    }

    public IReadOnlyList<uint> Clear()
    {
        var left = _visible.Keys.ToArray();
        _visible.Clear();
        return left;
    }
}
