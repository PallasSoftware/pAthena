using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// The ground-item visibility feed (PollGroundItemFeedAsync) on a REAL Orleans TestCluster with a deterministic clock at the
// production durations (pinned flooritem_lifetime 60s, unresolved-claim deadline 30s).
public sealed class WorldGroundItemFeedTests : IAsyncLifetime
{
    private static readonly WorldPresenceLeaseTests.OffsetClock Clock = new();
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        Clock.Reset();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<Configurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Grain => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>("prontera-region");

    private Task<WorldGroundItemDropResult> Drop(Guid? dropId = null, ushort x = 150) =>
        Grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", x, 180, new WorldGroundItemContent(501, 2), new WorldGroundItemOwnership(null), dropId ?? Guid.NewGuid()));

    private async Task<WorldGroundItemFeedCursor> Bootstrap()
    {
        var page = await Grain.PollGroundItemFeedAsync(null, "prontera");
        Assert.Equal(WorldGroundItemFeedStatus.Ready, page.Status);
        Assert.NotNull(page.Snapshot);
        return new(page.Epoch, page.AsOfSequence);
    }

    private async Task<WorldGroundItemReference> Reference(uint groundItemId) =>
        new("prontera", (await Grain.GetGroundItemsAsync("prontera")).Epoch, groundItemId);

    [Fact]
    public async Task Bootstrap_ReturnsTheCurrentItems_ThenADropAppearsIncrementally()
    {
        var existing = await Drop(x: 100);
        var bootstrap = await Grain.PollGroundItemFeedAsync(null, "prontera");
        Assert.Equal([existing.GroundItemId!.Value], bootstrap.Snapshot!.Select(v => v.GroundItemId));

        var dropped = await Drop(x: 151);
        var page = await Grain.PollGroundItemFeedAsync(new(bootstrap.Epoch, bootstrap.AsOfSequence), "prontera");

        var entry = Assert.Single(page.Entries!);
        Assert.Equal((WorldGroundItemFeedEntryKind.Appeared, dropped.GroundItemId!.Value), (entry.Kind, entry.GroundItemId));
        Assert.Equal((151, 180, 501, 2u), (entry.Item!.X, entry.Item.Y, entry.Item.Content.ItemId, entry.Item.Content.Amount));
    }

    [Fact]
    public async Task Pickup_AppendsRemoved_ButClaimAndReleaseAreNotVisibilityChanges()
    {
        var dropped = await Drop();
        var cursor = await Bootstrap();
        var reference = await Reference(dropped.GroundItemId!.Value);

        var released = Guid.NewGuid();
        await Grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, 1001, released));
        await Grain.ReleaseGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, released));
        Assert.Empty((await Grain.PollGroundItemFeedAsync(cursor, "prontera")).Entries!);

        var won = Guid.NewGuid();
        await Grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, 1002, won));
        await Grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, won));
        var entry = Assert.Single((await Grain.PollGroundItemFeedAsync(cursor, "prontera")).Entries!);

        Assert.Equal((WorldGroundItemFeedEntryKind.Removed, dropped.GroundItemId.Value, WorldGroundItemRemovalReason.PickedUp), (entry.Kind, entry.GroundItemId, entry.Reason));
        Assert.Null(entry.Item);
    }

    [Fact]
    public async Task Expiry_IsReportedByThePollItself_EvenWithNoOtherCall()
    {
        var dropped = await Drop();
        var cursor = await Bootstrap();

        Clock.Advance(TimeSpan.FromSeconds(61)); // pinned flooritem_lifetime 60000
        var entry = Assert.Single((await Grain.PollGroundItemFeedAsync(cursor, "prontera")).Entries!);

        Assert.Equal((WorldGroundItemFeedEntryKind.Removed, dropped.GroundItemId!.Value, WorldGroundItemRemovalReason.Expired), (entry.Kind, entry.GroundItemId, entry.Reason));
    }

    [Fact]
    public async Task AbandonedClaim_IsReportedAsClaimAbandoned()
    {
        var dropped = await Drop();
        var cursor = await Bootstrap();
        await Grain.ClaimGroundItemAsync(new WorldGroundItemClaim(await Reference(dropped.GroundItemId!.Value), 1001, Guid.NewGuid()));

        Clock.Advance(TimeSpan.FromSeconds(31)); // unresolved-claim deadline 30s
        var entry = Assert.Single((await Grain.PollGroundItemFeedAsync(cursor, "prontera")).Entries!);

        Assert.Equal(WorldGroundItemRemovalReason.ClaimAbandoned, entry.Reason);
    }

    [Fact]
    public async Task RetriedDrop_DoesNotAppearTwice()
    {
        var cursor = await Bootstrap();
        var dropId = Guid.NewGuid();
        await Drop(dropId);
        await Drop(dropId);

        Assert.Single((await Grain.PollGroundItemFeedAsync(cursor, "prontera")).Entries!);
    }

    [Fact]
    public async Task RePollingTheSameCursor_ReturnsTheSameEntries()
    {
        var cursor = await Bootstrap();
        await Drop();
        var first = await Grain.PollGroundItemFeedAsync(cursor, "prontera");
        var again = await Grain.PollGroundItemFeedAsync(cursor, "prontera");

        Assert.Equal(first.Entries!.Select(e => e.Sequence), again.Entries!.Select(e => e.Sequence));
    }

    [Fact]
    public async Task ForeignEpochOrFutureSequence_RequiresResyncWithAFullSnapshot()
    {
        var dropped = await Drop();
        var cursor = await Bootstrap();

        var foreignEpoch = await Grain.PollGroundItemFeedAsync(new(WorldSimulationEpoch.NewEpoch(), cursor.Sequence), "prontera");
        var future = await Grain.PollGroundItemFeedAsync(cursor with { Sequence = cursor.Sequence + 50 }, "prontera");

        foreach (var page in new[] { foreignEpoch, future })
        {
            Assert.Equal(WorldGroundItemFeedStatus.ResyncRequired, page.Status);
            Assert.Equal([dropped.GroundItemId!.Value], page.Snapshot!.Select(v => v.GroundItemId));
            Assert.Null(page.Entries);
        }
    }

    // ---- drop resolution and fencing (player-drop outbox) ----

    private async Task<WorldGroundItemDropResolutionResult> Resolve(Guid dropId, Guid? epoch = null) =>
        await Grain.ResolveGroundItemDropAsync(new WorldGroundItemDropResolution("prontera", dropId, epoch ?? (await Grain.GetGroundItemsAsync("prontera")).Epoch.Value));

    [Fact]
    public async Task ResolvingAnUnknownDrop_FencesIt_SoTheDropCanNeverHappenAfterwards()
    {
        var dropId = Guid.NewGuid();

        Assert.Equal(WorldGroundItemDropResolutionStatus.Fenced, (await Resolve(dropId)).Status);
        Assert.Equal(WorldGroundItemDropStatus.Fenced, (await Drop(dropId)).Status);
        Assert.Empty((await Grain.GetGroundItemsAsync("prontera")).Items);
        Assert.Equal(WorldGroundItemDropResolutionStatus.Fenced, (await Resolve(dropId)).Status); // stable answer
    }

    [Fact]
    public async Task ResolvablePlayerDrop_IsStillKnownAfterItWasPickedUpAndTheShortDedupWindowPassed()
    {
        var dropId = Guid.NewGuid();
        var resolvable = await Grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, new WorldGroundItemContent(501, 1), new WorldGroundItemOwnership(null), dropId, Resolvable: true));
        var plain = await Drop();
        var reference = await Reference(resolvable.GroundItemId!.Value);
        var claimId = Guid.NewGuid();
        await Grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, 1001, claimId));
        await Grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, claimId));

        Clock.Advance(TimeSpan.FromHours(2)); // past flooritem_lifetime and the 30s dedup window

        var answer = await Resolve(dropId);
        Assert.Equal((WorldGroundItemDropResolutionStatus.Dropped, resolvable.GroundItemId), (answer.Status, answer.GroundItemId));
        Assert.NotNull(plain.GroundItemId);
    }

    [Fact]
    public async Task UnknownDropAfterTheEpochChanged_IsReportedAsSuch_AndStillFenced()
    {
        var dropId = Guid.NewGuid();

        Assert.Equal(WorldGroundItemDropResolutionStatus.EpochChanged, (await Resolve(dropId, Guid.NewGuid())).Status);
        Assert.Equal(WorldGroundItemDropStatus.Fenced, (await Drop(dropId)).Status);
    }

    public sealed class Configurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services.AddSingleton<IWorldPartitionResolver>(
                WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>()
                .AddSingleton<IMapCollisionProvider>(EmptyMapCollisionProvider.Instance)
                .AddSingleton<TimeProvider>(Clock);
        }
    }
}
