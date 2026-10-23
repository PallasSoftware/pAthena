using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

public sealed class GroundItemFeedMirrorTests
{
    private static readonly WorldSimulationEpoch Epoch = WorldSimulationEpoch.NewEpoch();

    private static WorldGroundItemView View(uint id, ushort x = 100, ushort y = 100) =>
        new(id, x, y, new WorldGroundItemContent(909, 1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1));

    private static WorldGroundItemFeedPage Snapshot(long asOf, params WorldGroundItemView[] items) =>
        new("izlude", Epoch, WorldGroundItemFeedStatus.Ready, items, null, asOf);

    private static WorldGroundItemFeedPage Entries(long asOf, params WorldGroundItemFeedEntry[] entries) =>
        new("izlude", Epoch, WorldGroundItemFeedStatus.Ready, null, entries, asOf);

    private static WorldGroundItemFeedEntry Appeared(long seq, uint id) => new(seq, WorldGroundItemFeedEntryKind.Appeared, id, View(id), null);
    private static WorldGroundItemFeedEntry Removed(long seq, uint id) => new(seq, WorldGroundItemFeedEntryKind.Removed, id, null, WorldGroundItemRemovalReason.PickedUp);

    [Fact]
    public void ReplayedEntries_AreIgnored_SoARemovedItemNeverReappears()
    {
        var mirror = new GroundItemFeedMirror();
        mirror.Apply(Snapshot(0));
        mirror.Apply(Entries(2, Appeared(1, 7), Removed(2, 7)));

        var replay = mirror.Apply(Entries(2, Appeared(1, 7))); // a duplicated, older page

        Assert.Empty(replay);
        Assert.Empty(mirror.Items);
        Assert.Equal(2, mirror.Cursor!.Value.Sequence);
    }

    [Fact]
    public void StaleSnapshotOfTheSameEpoch_IsIgnored()
    {
        var mirror = new GroundItemFeedMirror();
        mirror.Apply(Snapshot(0));
        mirror.Apply(Entries(2, Appeared(1, 7), Removed(2, 7)));

        Assert.Empty(mirror.Apply(Snapshot(1, View(7)))); // taken before the removal
        Assert.Empty(mirror.Items);
    }

    [Fact]
    public void ResyncSnapshot_ReplacesTheMirrorByDiff()
    {
        var mirror = new GroundItemFeedMirror();
        mirror.Apply(Snapshot(3, View(1), View(2)));

        var changes = mirror.Apply(new("izlude", WorldSimulationEpoch.NewEpoch(), WorldGroundItemFeedStatus.ResyncRequired, [View(2), View(3)], null, 9));

        Assert.Equal([(1u, true), (3u, false)], changes.Select(c => (c.GroundItemId, c.IsRemoval)).OrderBy(c => c.GroundItemId));
        Assert.Equal([2u, 3u], mirror.Items.Select(i => i.GroundItemId).Order());
    }

    [Fact]
    public void EntriesBeforeAnyBootstrap_OrFromAnotherEpoch_AreNotApplied()
    {
        var mirror = new GroundItemFeedMirror();
        Assert.Empty(mirror.Apply(Entries(1, Appeared(1, 7))));
        mirror.Apply(Snapshot(0));
        Assert.Empty(mirror.Apply(new("izlude", WorldSimulationEpoch.NewEpoch(), WorldGroundItemFeedStatus.Ready, null, [Appeared(1, 8)], 1)));
        Assert.Empty(mirror.Items);
    }

    [Fact]
    public void SessionVisibility_UsesThePinnedAreaSquare_AndReportsEnterAndLeave()
    {
        var visibility = new GroundItemSessionVisibility();
        IReadOnlyCollection<WorldGroundItemView> items = [View(1, 114, 86), View(2, 115, 100)]; // |dx|=14,|dy|=14 in; |dx|=15 out

        var (entered, left) = visibility.Reconcile(items, 100, 100, areaSize: 14);
        Assert.Equal([1u], entered.Select(i => i.GroundItemId));
        Assert.Empty(left);

        (entered, left) = visibility.Reconcile(items, 101, 100, areaSize: 14);
        Assert.Equal([2u], entered.Select(i => i.GroundItemId));
        Assert.Empty(left);

        (entered, left) = visibility.Reconcile([items.Last()], 101, 100, areaSize: 14); // item 1 picked up
        Assert.Empty(entered);
        Assert.Equal([1u], left);
    }
}
