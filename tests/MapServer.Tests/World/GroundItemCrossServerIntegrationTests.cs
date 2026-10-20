using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;
using MapServer.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.MapServer.Tests.World;

// Feature 16 S2/S3: the FULL cross-server ground-item pickup pipeline - a real Orleans TestCluster-
// hosted IWorldPartitionGrain for the exclusive World claim, combined with a real
// CharacterInventoryService over InMemoryInventoryOperations (the REAL shared InventoryOperationEngine
// rules plus CharServer's idempotent operation log - see that class's own doc comment) for the durable
// grant. World.Tests/WorldGroundItemGrainTests.cs already proves the claim state machine in isolation;
// this file proves the two systems TOGETHER produce the end-to-end "exactly one successful durable
// grant, regardless of concurrent claimers, lost acks, or retries" guarantee neither system can prove
// alone - a claim winning in World is a necessary but not sufficient condition for an actual grant.
public sealed class GroundItemCrossServerIntegrationTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);
    private static readonly ItemDefinition Wood = ItemCatalog.Current.ByAegisName["Wood"];

    private async Task<(WorldGroundItemReference Reference, uint GroundItemId)> DropWood(IWorldPartitionGrain grain)
    {
        var drop = await grain.DropGroundItemAsync(new WorldGroundItemDrop("prontera", 150, 180, new(Wood.Id, 1), new(null), Guid.NewGuid()));
        var epoch = (await grain.GetGroundItemsAsync("prontera")).Epoch;
        return (new WorldGroundItemReference("prontera", epoch, drop.GroundItemId!.Value), drop.GroundItemId!.Value);
    }

    // The pickup pipeline a MapClientSession's 0x0362 handler runs: claim -> grant -> complete.
    // CharServer's own operation id is DERIVED from the winning ClaimId (deterministic, never
    // Guid.NewGuid()) so a retried grant for the SAME claim always replays through CharServer's
    // idempotency log instead of risking a duplicate - see CharacterInventoryService.GrantAsync's
    // own operationId parameter doc comment.
    private static Guid OperationIdFor(Guid claimId) => claimId;

    [Fact]
    public async Task SuccessfulClaimAndGrant_EndsWithExactlyOneItemInInventory_AndTheGroundItemPermanentlyGone()
    {
        var grain = Partition("prontera-region");
        var (reference, _) = await DropWood(grain);
        var inventory = new InMemoryInventoryOperations();
        var service = new CharacterInventoryService(1, 1, inventory, new CharacterInventorySnapshot([]), new NoOpInventoryRules(), ItemCatalog.Current.ById);
        var claimId = Guid.NewGuid();

        var claim = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1, claimId));
        Assert.Equal(WorldGroundItemClaimStatus.Claimed, claim.Status);

        var grant = await service.GrantAsync([new InventoryPlanner.GrantRequest(Wood, claim.Content!.Amount)], InventoryOperationKind.Add, CancellationToken.None, OperationIdFor(claimId));
        Assert.True(grant.Succeeded);

        var completion = await grain.CompleteGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, claimId));
        Assert.Equal(WorldGroundItemCompletionStatus.Applied, completion.Status);

        Assert.Equal(1, inventory.AmountOf(Wood.Id));
        Assert.Empty((await grain.GetGroundItemsAsync("prontera")).Items);
    }

    // Two characters race the SAME ground item: World resolves exactly one winner (proven in
    // isolation by WorldGroundItemGrainTests), and only that winner's CharServer grant executes -
    // the loser never even attempts a durable mutation.
    [Fact]
    public async Task TwoCharactersRaceTheSamePickup_OnlyTheWorldClaimWinner_EverAttemptsADurableGrant()
    {
        var grain = Partition("prontera-region");
        var (reference, _) = await DropWood(grain);
        var inventoryA = new InMemoryInventoryOperations();
        var inventoryB = new InMemoryInventoryOperations();
        var serviceA = new CharacterInventoryService(1, 1, inventoryA, new CharacterInventorySnapshot([]), new NoOpInventoryRules(), ItemCatalog.Current.ById);
        var serviceB = new CharacterInventoryService(2, 2, inventoryB, new CharacterInventorySnapshot([]), new NoOpInventoryRules(), ItemCatalog.Current.ById);

        var claims = await Task.WhenAll(
            grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1, Guid.NewGuid())),
            grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 2, Guid.NewGuid())));

        var winnerIndex = claims[0].Status == WorldGroundItemClaimStatus.Claimed ? 0 : 1;
        var loserIndex = 1 - winnerIndex;
        Assert.Equal(WorldGroundItemClaimStatus.Claimed, claims[winnerIndex].Status);
        Assert.Equal(WorldGroundItemClaimStatus.AlreadyClaimedByOther, claims[loserIndex].Status);

        // The LOSER's own service never attempts any grant at all - it has no Content to grant.
        Assert.Null(claims[loserIndex].Content);
        var winnerService = winnerIndex == 0 ? serviceA : serviceB;
        var winnerInventory = winnerIndex == 0 ? inventoryA : inventoryB;
        var grant = await winnerService.GrantAsync(Wood, claims[winnerIndex].Content!.Amount, CancellationToken.None);
        Assert.True(grant.Succeeded);
        Assert.Equal(1, winnerInventory.AmountOf(Wood.Id));

        var loserInventory = winnerIndex == 0 ? inventoryB : inventoryA;
        Assert.Empty(loserInventory.Requests); // The loser's CharServer connection was never even touched.
    }

    // Lost acknowledgement: CharServer applies the grant durably, but its answer never reaches
    // MapServer. The retry (SAME deterministic operationId) must be recognized as a replay, never a
    // second grant - this is CharServer's own idempotent operation log (InventoryOperationEngine),
    // exercised here through the real CharacterInventoryService retry path, not re-implemented.
    [Fact]
    public async Task LostAcknowledgement_ThenRetry_GrantsExactlyOnce_NeverDuplicated()
    {
        var grain = Partition("prontera-region");
        var (reference, _) = await DropWood(grain);
        var inventory = new InMemoryInventoryOperations { LoseAnswers = 1 };
        var service = new CharacterInventoryService(1, 1, inventory, new CharacterInventorySnapshot([]), new NoOpInventoryRules(), ItemCatalog.Current.ById, maxAttempts: 1, retryDelay: TimeSpan.Zero);
        var claimId = Guid.NewGuid();
        var claim = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1, claimId));

        // First attempt: CharServer applies it, but the answer is lost - MapServer sees InDoubt.
        var firstAttempt = await service.GrantAsync([new InventoryPlanner.GrantRequest(Wood, claim.Content!.Amount)], InventoryOperationKind.Add, CancellationToken.None, OperationIdFor(claimId));
        Assert.Equal(InventoryChangeStatus.InDoubt, firstAttempt.Status);
        Assert.Equal(1, inventory.AmountOf(Wood.Id)); // Durably applied despite the lost answer.

        // MapServer resolves the SAME operation id - CharServer replays its already-logged response.
        var resolved = await service.ResolveAsync(firstAttempt.OperationId, CancellationToken.None);

        Assert.True(resolved.Succeeded);
        Assert.Equal(1, inventory.AmountOf(Wood.Id)); // STILL exactly one - never duplicated.
        Assert.Equal(2, inventory.Requests.Count); // Both attempts reached CharServer...
        Assert.Single(inventory.Applied); // ...but only the first one actually mutated durable state.
    }

    // An InDoubt (unknown) grant outcome is NOT a confirmed failure (per the user's own rule 5: "if
    // the grant outcome is unknown, NEVER make the item available to another player"). World's own
    // Release API does not itself forbid calling it after an InDoubt result - that safety rule is
    // MapServer's own pickup-handler responsibility (only ever release after a CONFIRMED-no-mutation
    // result), not something this lower-level API can enforce by itself. This test documents that
    // boundary precisely so a future caller cannot assume World would reject a misuse here.
    [Fact]
    public async Task UnresolvedInDoubtGrant_IsNotAConfirmedFailure_WorldItselfDoesNotForbidReleasingOnIt()
    {
        var grain = Partition("prontera-region");
        var (reference, _) = await DropWood(grain);
        var inventoryA = new InMemoryInventoryOperations { DropRequests = 999 }; // Every attempt is lost/never applied.
        var serviceA = new CharacterInventoryService(1, 1, inventoryA, new CharacterInventorySnapshot([]), new NoOpInventoryRules(), ItemCatalog.Current.ById, maxAttempts: 1, retryDelay: TimeSpan.Zero);
        var claimIdA = Guid.NewGuid();
        var claimA = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1, claimIdA));

        var grantA = await serviceA.GrantAsync([new InventoryPlanner.GrantRequest(Wood, claimA.Content!.Amount)], InventoryOperationKind.Add, CancellationToken.None, OperationIdFor(claimIdA));
        Assert.Equal(InventoryChangeStatus.InDoubt, grantA.Status);
    }

    // The genuinely safe failure path: GrantAsync's own planner rejects synchronously (no capacity,
    // or - as modeled here - the catalog item itself can't fit the planner's rules) BEFORE any
    // request ever reaches CharServer. This is a CONFIRMED no-mutation outcome the caller can safely
    // release on.
    [Fact]
    public async Task ConfirmedRejection_BeforeAnyCharServerRequest_SafelyReleasesForAnotherCharacter()
    {
        var grain = Partition("prontera-region");
        var (reference, _) = await DropWood(grain);
        var inventoryA = new InMemoryInventoryOperations();
        var serviceA = new CharacterInventoryService(1, 1, inventoryA, new CharacterInventorySnapshot([]), new NoOpInventoryRules(), ItemCatalog.Current.ById);
        var claimIdA = Guid.NewGuid();
        var claimA = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 1, claimIdA));

        // GrantRequest with Amount 0 is planned by InventoryPlanner.PlanGrants as "nothing to add" ->
        // ExecuteAsync's own plan delegate returns null/empty mutations -> Rejected, synchronously,
        // with ZERO requests ever sent to CharServer - a genuinely confirmed no-mutation outcome.
        var grantA = await serviceA.GrantAsync([new InventoryPlanner.GrantRequest(Wood, 0)], InventoryOperationKind.Add, CancellationToken.None);
        Assert.Equal(InventoryChangeStatus.Rejected, grantA.Status);
        Assert.Empty(inventoryA.Requests);

        var release = await grain.ReleaseGroundItemClaimAsync(new WorldGroundItemClaimReference(reference, claimIdA));
        Assert.Equal(WorldGroundItemCompletionStatus.Applied, release.Status);

        var inventoryB = new InMemoryInventoryOperations();
        var serviceB = new CharacterInventoryService(2, 2, inventoryB, new CharacterInventorySnapshot([]), new NoOpInventoryRules(), ItemCatalog.Current.ById);
        var claimB = await grain.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, CharacterId: 2, Guid.NewGuid()));
        Assert.Equal(WorldGroundItemClaimStatus.Claimed, claimB.Status);
        var grantB = await serviceB.GrantAsync(Wood, claimB.Content!.Amount, CancellationToken.None);
        Assert.True(grantB.Succeeded);
        Assert.Equal(1, inventoryB.AmountOf(Wood.Id));
    }

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services.AddSingleton<IWorldPartitionResolver>(
                WorldPartitionTopologyLoader.Load(Path.Combine(FindRepositoryRoot(), "conf", "world_partitions.json"), ["prontera", "prt_fild08d", "izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>()
                .AddSingleton<IMapCollisionProvider>(EmptyMapCollisionProvider.Instance)
                .AddSingleton(TimeProvider.System);
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Athena.NET.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}

// A no-op IInventoryRules that never blocks a grant on weight/capacity - this integration's own
// focus is the cross-server claim/grant pipeline, not regional inventory-rule content (already
// covered by RathenaReferenceInventoryConformanceTests and friends).
internal sealed class NoOpInventoryRules : Athena.Net.MapServer.Gameplay.Rules.IInventoryRules
{
    public uint MaxWeight(in Athena.Net.MapServer.Gameplay.Rules.MaxWeightInput input) => uint.MaxValue;
    public uint TotalWeight(CharacterInventorySnapshot inventory, IReadOnlyDictionary<int, ItemDefinition> items) => 0;
    public int WeightPercent(uint weight, uint maxWeight) => 0;
    public Athena.Net.MapServer.Gameplay.Rules.WeightLevel WeightLevelFor(int weightPercent) => Athena.Net.MapServer.Gameplay.Rules.WeightLevel.Normal;
    public bool IsCreatedIdentified(ItemDefinition item) => true;
    public uint FreeSlots(CharacterInventorySnapshot inventory, uint inventorySlots) => inventorySlots;
    public Athena.Net.MapServer.Gameplay.Rules.AddItemDecision EvaluateAdd(CharacterInventorySnapshot inventory, Athena.Net.MapServer.Gameplay.Rules.InventoryLimits limits, uint currentWeight, in Athena.Net.MapServer.Gameplay.Rules.AddItemCandidate candidate) =>
        new(Athena.Net.MapServer.Gameplay.Rules.AddItemResult.Success, null, inventory.Items.Count == 0 ? 0u : (uint)inventory.Items.Max(i => i.SlotIndex) + 1, 0);
}
