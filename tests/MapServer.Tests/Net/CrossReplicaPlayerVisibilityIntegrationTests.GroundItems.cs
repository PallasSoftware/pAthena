using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Cross-replica ground-item visibility through the production tick: an item dropped through replica A's own World runtime
// (the path a monster death on replica A takes) must become visible to a session connected to replica B, follow that
// session's movement in and out of AREA_SIZE, and vanish when picked up through replica A - with replica B holding no
// ground-item state other than its feed mirror. Client projection is Protocol-pending-capture (0x0ADD); these tests assert
// the server-side visible sets.
public sealed partial class CrossReplicaPlayerVisibilityIntegrationTests
{
    private static Task<WorldGroundItemDropResult> DropThrough(Replica replica, string mapId, ushort x, ushort y) =>
        replica.Runtime.DropGroundItemAsync(new WorldGroundItemDrop(mapId, x, y, new WorldGroundItemContent(909, 1), new WorldGroundItemOwnership(null), Guid.NewGuid()), CancellationToken.None);

    private static async Task TickUntilAsync(Replica replica, MapClientSession session, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do { await TickAsync(replica, session); if (condition()) return; await Task.Delay(50); }
        while (DateTime.UtcNow < deadline);
        Assert.True(condition());
    }

    [Fact]
    public async Task GroundItemDroppedThroughReplicaA_IsVisibleOnReplicaB_UntilPickedUpThroughReplicaA()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var b = await ConnectAsync(replicaB, accountId: 41, mapId, 101, 100);
        using var _b = b.Client;

        var dropped = await DropThrough(replicaA, mapId, 105, 100);
        var id = dropped.GroundItemId!.Value;
        await TickUntilAsync(replicaB, b.Session, () => b.Session.GroundItems.VisibleIds.Contains(id));

        // Replica A wins the World claim and completes it (its durable grant is out of scope here).
        var reference = new WorldGroundItemReference(mapId, (await replicaA.Runtime.GetGroundItemsAsync(mapId, CancellationToken.None)).Epoch, id);
        var claimId = Guid.NewGuid();
        Assert.Equal(WorldGroundItemClaimStatus.Claimed, (await replicaA.Runtime.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, 7, claimId), CancellationToken.None)).Status);
        // A concurrent pickup attempt from replica B's player loses: exactly one claimant.
        Assert.Equal(WorldGroundItemClaimStatus.AlreadyClaimedByOther, (await replicaB.Runtime.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, 41, Guid.NewGuid()), CancellationToken.None)).Status);
        Assert.Contains(id, b.Session.GroundItems.VisibleIds); // still physically there until completed
        await replicaA.Runtime.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, claimId), CancellationToken.None);

        await TickUntilAsync(replicaB, b.Session, () => !b.Session.GroundItems.VisibleIds.Contains(id));
        Assert.True(replicaB.Server.TryGetGroundItemMirror(mapId, out var mirror));
        Assert.DoesNotContain(mirror.Items, item => item.GroundItemId == id);
    }

    [Fact]
    public async Task GroundItemOutsideAreaSize_BecomesVisibleOnlyWhenTheObserverWalksIntoRange()
    {
        const string mapId = "izlude";
        var replicaA = MakeReplica(mapId);
        var replicaB = MakeReplica(mapId);
        var b = await ConnectAsync(replicaB, accountId: 42, mapId, 101, 100);
        using var _b = b.Client;

        // 20 cells north: outside AREA_SIZE (14).
        var far = (await DropThrough(replicaA, mapId, 101, 120)).GroundItemId!.Value;
        var near = (await DropThrough(replicaA, mapId, 103, 100)).GroundItemId!.Value;
        await TickUntilAsync(replicaB, b.Session, () => b.Session.GroundItems.VisibleIds.Contains(near));
        Assert.DoesNotContain(far, b.Session.GroundItems.VisibleIds);

        await b.Stream.WriteAsync(BuildMovementRequest(101, 115));
        await TickUntilAsync(replicaB, b.Session, () => b.Session.GroundItems.VisibleIds.Contains(far) && !b.Session.GroundItems.VisibleIds.Contains(near));
    }
}
