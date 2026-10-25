using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;
using MapServer.Tests.Testing;
using Orleans.TestingHost;
using static Athena.Net.MapServer.World.CharacterItemDropService;

namespace Athena.Net.MapServer.Tests.World;

// Player item drop (pinned pc_dropitem / pc_candrop) across both authorities - the REAL World grain on a TestCluster and
// CharServer's real operation rules with its outbox semantics (InMemoryInventoryOperations) - under injected faults: a World
// request lost before or after it was applied, a definitive refusal, an unreachable resolution, lost CharServer answers, a
// MapServer "crash" (a fresh service over the same durable rows) and racing settlers. The invariant every test checks: the
// units the player owned are always accounted for exactly once - in the inventory, in the outbox, or on the ground.
public sealed class CharacterItemDropServiceTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<GroundItemCrossServerIntegrationTests.TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items = ItemCatalog.Current.ById;
    private const int RedPotion = 501, Knife = 1201, ThiefBugCard = 4016;
    private const string Map = "prontera";

    private IWorldRuntime RealWorld() => new OrleansWorldRuntime(_cluster.Client,
        WorldPartitionTopologyLoader.Load(Path.Combine(RepositoryRoot(), "conf", "world_partitions.json"), ["prontera", "prt_fild08d", "izlude", "geffen"]));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        return dir!.FullName;
    }

    public enum DropFault { None, LostBeforeApplied, AppliedThenAckLost, MapNotFound }

    // The real runtime with scripted faults: each DropGroundItemAsync call takes the next DropFaults entry (None once exhausted);
    // ResolveFailures resolution calls throw before reaching World; a set ResolveOverride replaces World's answer.
    public class FaultyWorld : System.Reflection.DispatchProxy
    {
        public IWorldRuntime Inner { get; set; } = null!;
        public Queue<DropFault> DropFaults { get; } = new();
        public int ResolveFailures { get; set; }
        public bool GroundReadFails { get; set; }
        public WorldGroundItemDropResolutionStatus? ResolveOverride { get; set; }
        public int DropCalls { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IWorldRuntime.DropGroundItemAsync) => DropAsync((WorldGroundItemDrop)args![0]!, (CancellationToken)args[1]!),
            nameof(IWorldRuntime.ResolveGroundItemDropAsync) => ResolveAsync((WorldGroundItemDropResolution)args![0]!, (CancellationToken)args[1]!),
            nameof(IWorldRuntime.GetGroundItemsAsync) when GroundReadFails => Task.FromException<WorldGroundItemSnapshot>(new TimeoutException("world down")),
            _ => method.Invoke(Inner, args),
        };

        private async Task<WorldGroundItemDropResult> DropAsync(WorldGroundItemDrop drop, CancellationToken cancellationToken)
        {
            DropCalls++;
            switch (DropFaults.Count > 0 ? DropFaults.Dequeue() : DropFault.None)
            {
                case DropFault.LostBeforeApplied: throw new TimeoutException("drop lost before World saw it");
                case DropFault.AppliedThenAckLost: await Inner.DropGroundItemAsync(drop, cancellationToken); throw new TimeoutException("drop applied, ack lost");
                case DropFault.MapNotFound: return new WorldGroundItemDropResult(WorldGroundItemDropStatus.MapNotFound, null);
                default: return await Inner.DropGroundItemAsync(drop, cancellationToken);
            }
        }

        private async Task<WorldGroundItemDropResolutionResult> ResolveAsync(WorldGroundItemDropResolution resolution, CancellationToken cancellationToken)
        {
            if (ResolveFailures > 0) { ResolveFailures--; throw new TimeoutException("resolution lost"); }
            if (ResolveOverride is { } status) return new WorldGroundItemDropResolutionResult(status, null);
            return await Inner.ResolveGroundItemDropAsync(resolution, cancellationToken);
        }
    }

    private static CharacterInventoryItem Row(uint id, int itemId, uint amount = 1, uint equip = 0, byte refine = 0, byte bound = 0, uint equipSwitch = 0, uint expire = 0, uint card0 = 0) =>
        new(id, id - 1, itemId, amount, equip, true, refine, 0, bound, ItemInstanceDetail.Empty with { Card0 = card0, EquipSwitch = equipSwitch, ExpireTime = expire, Revision = 1 });

    private sealed record Fixture(InMemoryInventoryOperations Operations, CharacterItemDropService Service, FaultyWorld World, IWorldRuntime Real);

    private Fixture Setup(params CharacterInventoryItem[] rows)
    {
        var snapshot = new CharacterInventorySnapshot(rows);
        var operations = new InMemoryInventoryOperations(snapshot);
        var real = RealWorld();
        var faulty = (FaultyWorld)(object)System.Reflection.DispatchProxy.Create<IWorldRuntime, FaultyWorld>();
        faulty.Inner = real;
        var inventory = TestInventory.Service(7, 9, operations, snapshot);
        return new(operations, new CharacterItemDropService(inventory, (IWorldRuntime)(object)faulty), faulty, real);
    }

    // A MapServer restart: nothing in memory survives; a new session's service reads CharServer's durable rows again.
    private static CharacterItemDropService AfterRestart(Fixture f, IWorldRuntime world) =>
        new(TestInventory.Service(7, 9, f.Operations, CharacterInventorySnapshot.FromRows(f.Operations.Rows)), world);

    private async Task<IReadOnlyList<WorldGroundItemView>> Ground(Fixture f) => (await f.Real.GetGroundItemsAsync(Map, CancellationToken.None)).Items;

    private static uint Units(Fixture f, int itemId) =>
        (uint)f.Operations.AmountOf(itemId) + (uint)f.Operations.ContainerRows(InventoryContainer.PendingGroundDrop).Where(r => r.ItemId == itemId).Sum(r => (long)r.Amount);

    private static Guid DropIdOf(Fixture f) => f.Operations.Applied.First(r => r.RecordPendingDrop is not null).RecordPendingDrop!.DropId;

    [Fact]
    public async Task Drop_RemovesTheUnits_PutsExactlyThemOnTheGround_AndSettlesTheOutbox()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));

        var (result, groundItemId) = await f.Service.DropAsync(0, 3, Map, 150, 180, Items, CancellationToken.None);

        Assert.Equal(DropResult.Dropped, result);
        Assert.Equal(2u, Units(f, RedPotion));
        var ground = Assert.Single(await Ground(f));
        Assert.Equal((groundItemId!.Value, RedPotion, 3u, (ushort)150, (ushort)180), (ground.GroundItemId, ground.Content.ItemId, ground.Content.Amount, ground.X, ground.Y));
        Assert.Empty(f.Operations.PendingDrops);
        Assert.Empty(f.Operations.ContainerRows(InventoryContainer.PendingGroundDrop));
    }

    [Fact]
    public async Task DroppedInstance_IsGrantedBackIntact_ToWhoeverPicksItUp()
    {
        var f = Setup(Row(1, Knife, refine: 7, card0: ThiefBugCard));
        var (_, groundItemId) = await f.Service.DropAsync(0, 1, Map, 150, 180, Items, CancellationToken.None);

        var picker = TestInventory.Service(8, 10, new InMemoryInventoryOperations(new CharacterInventorySnapshot([])), new CharacterInventorySnapshot([]));
        var reference = new WorldGroundItemReference(Map, (await f.Real.GetGroundItemsAsync(Map, CancellationToken.None)).Epoch, groundItemId!.Value);
        var claim = await f.Real.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, 10, Guid.NewGuid()), CancellationToken.None);
        var grant = await picker.GrantAsync([new InventoryPlanner.GrantRequest(Items[Knife], 1, PickupTemplate(claim.Content!))], InventoryOperationKind.Pickup, CancellationToken.None);

        var picked = Assert.Single(grant.Snapshot.Items);
        Assert.Equal((Knife, (byte)7, (uint)ThiefBugCard, 0u, 0u), (picked.ItemId, picked.Refine, picked.Details.Card0, picked.Equip, picked.Details.EquipSwitch));
    }

    public static TheoryData<string> Refusals => new() { "equipped", "switch", "bound", "rental", "nodrop", "nodropmap", "amount", "unknown" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task PinnedRefusals_ChangeNothingAnywhere(string refusal)
    {
        var noDrop = Items.Values.First(i => i is EtcItemDefinition && i.Meta is { NoDrop: true, TradeOverride: > 0 });
        var row = refusal switch
        {
            "equipped" => Row(1, Knife, equip: 0x2),
            "switch" => Row(1, Knife, equipSwitch: 0x2),
            "bound" => Row(1, Knife, bound: 4),
            "rental" => Row(1, Knife, expire: 2_000_000_000),
            "nodrop" => Row(1, noDrop.Id),
            _ => Row(1, RedPotion, amount: 2),
        };
        var f = Setup(row);

        var (result, _) = await f.Service.DropAsync(refusal == "unknown" ? 9u : 0u, refusal == "amount" ? 3u : 1u, Map, 150, 180, Items, CancellationToken.None,
            noDropMap: refusal == "nodropmap");

        Assert.NotEqual(DropResult.Dropped, result);
        Assert.Empty(f.Operations.Requests);
        Assert.Empty(await Ground(f));
    }

    [Fact]
    public async Task WorldUnreachableBeforeAnything_ChangesNothing()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        f.World.GroundReadFails = true;

        Assert.Equal(DropResult.WorldUnavailable, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        Assert.Empty(f.Operations.Requests);
        Assert.Equal(5u, Units(f, RedPotion));
    }

    [Fact]
    public async Task LostAcknowledgementOfTheRemoval_IsReplayed_AndDropsOnce()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        f.Operations.LoseAnswers = 1;

        Assert.Equal(DropResult.Dropped, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        Assert.Equal(3u, Units(f, RedPotion));
        Assert.Single(await Ground(f));
    }

    [Fact]
    public async Task RemovalNeverReachingCharServer_IsPendingButNothingMoved_AndRecoveryFindsNothing()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        f.Operations.DropRequests = 3;

        Assert.Equal(DropResult.Pending, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        Assert.Empty(await AfterRestart(f, f.Real).RecoverAsync(Items, CancellationToken.None));
        Assert.Equal(5u, (uint)f.Operations.AmountOf(RedPotion));
        Assert.Empty(await Ground(f));
    }

    [Fact]
    public async Task WorldAppliedTheDropButEveryAckWasLost_IsResolvedAsDropped_NeverReturned()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        for (var i = 0; i < 3; i++) f.World.DropFaults.Enqueue(DropFault.AppliedThenAckLost);

        Assert.Equal(DropResult.Dropped, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        var ground = Assert.Single(await Ground(f)); // the three retries were one idempotent drop
        Assert.Equal(2u, ground.Content.Amount);
        Assert.Equal(3u, Units(f, RedPotion));       // 3 owned + 2 on the ground = the original 5
        Assert.Empty(f.Operations.PendingDrops);
    }

    [Fact]
    public async Task DropThatNeverReachedWorld_IsFencedAndReturnedIntact_AndALateCopyCanNeverCreateIt()
    {
        var f = Setup(Row(1, Knife, refine: 7, card0: ThiefBugCard));
        for (var i = 0; i < 3; i++) f.World.DropFaults.Enqueue(DropFault.LostBeforeApplied);

        Assert.Equal(DropResult.Returned, (await f.Service.DropAsync(0, 1, Map, 150, 180, Items, CancellationToken.None)).Result);
        var restored = Assert.Single(f.Operations.Rows);
        Assert.Equal((Knife, (byte)7, (uint)ThiefBugCard), ((int)restored.ItemId, restored.Refine, restored.Card0));
        Assert.Empty(f.Operations.PendingDrops);

        // A delayed copy of the original drop request arriving now is refused by the fence.
        var late = await f.Real.DropGroundItemAsync(new WorldGroundItemDrop(Map, 150, 180, new WorldGroundItemContent(Knife, 1), new WorldGroundItemOwnership(null), DropIdOf(f), Resolvable: true), CancellationToken.None);
        Assert.Equal(WorldGroundItemDropStatus.Fenced, late.Status);
        Assert.Empty(await Ground(f));
    }

    [Fact]
    public async Task DefinitiveRefusal_IsSettledThroughTheFence_AndReturned()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        f.World.DropFaults.Enqueue(DropFault.MapNotFound);

        Assert.Equal(DropResult.Returned, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        Assert.Equal(5u, (uint)f.Operations.AmountOf(RedPotion));
        Assert.Empty(await Ground(f));
    }

    [Fact]
    public async Task WorldOutageThroughResolution_KeepsTheUnitsDurablyInTheOutbox_AndRecoveryAfterRestartReturnsThem()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        for (var i = 0; i < 3; i++) f.World.DropFaults.Enqueue(DropFault.LostBeforeApplied);
        f.World.ResolveFailures = 1;

        Assert.Equal(DropResult.Pending, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        Assert.Equal(3u, (uint)f.Operations.AmountOf(RedPotion));
        Assert.Equal(5u, Units(f, RedPotion)); // not lost: 2 held in the outbox
        Assert.Single(f.Operations.PendingDrops);

        var recovered = await AfterRestart(f, f.Real).RecoverAsync(Items, CancellationToken.None);

        Assert.Equal(DropResult.Returned, Assert.Single(recovered).Result);
        Assert.Equal(5u, (uint)f.Operations.AmountOf(RedPotion));
        Assert.Empty(f.Operations.PendingDrops);
        Assert.Empty(await Ground(f));
    }

    [Fact]
    public async Task CrashAfterWorldDroppedIt_RecoveryCompletes_NeverDuplicates()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        for (var i = 0; i < 3; i++) f.World.DropFaults.Enqueue(DropFault.AppliedThenAckLost);
        f.World.ResolveFailures = 1;

        Assert.Equal(DropResult.Pending, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        var recovered = await AfterRestart(f, f.Real).RecoverAsync(Items, CancellationToken.None);

        Assert.Equal(DropResult.Dropped, Assert.Single(recovered).Result);
        Assert.Equal(3u, Units(f, RedPotion));
        Assert.Equal(2u, Assert.Single(await Ground(f)).Content.Amount);
        Assert.Empty(f.Operations.PendingDrops);
    }

    [Fact]
    public async Task LostSettlementAnswers_AreReplayedByRecovery_UnderTheSameOperationId()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        f.Operations.DropWhen = request => request.ResolvesPendingDrop != Guid.Empty;

        Assert.Equal(DropResult.Pending, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        Assert.Single(await Ground(f));
        f.Operations.DropWhen = null;

        Assert.Equal(DropResult.Dropped, Assert.Single(await AfterRestart(f, f.Real).RecoverAsync(Items, CancellationToken.None)).Result);
        Assert.Equal(3u, Units(f, RedPotion));
        Assert.Single(f.Operations.Applied, r => r.ResolvesPendingDrop != Guid.Empty);
    }

    [Fact]
    public async Task TwoMapServersRecoveringTheSameDrop_SettleItExactlyOnce()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        for (var i = 0; i < 3; i++) f.World.DropFaults.Enqueue(DropFault.LostBeforeApplied);
        f.World.ResolveFailures = 1;
        await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None);

        var outcomes = await Task.WhenAll(AfterRestart(f, f.Real).RecoverAsync(Items, CancellationToken.None), AfterRestart(f, f.Real).RecoverAsync(Items, CancellationToken.None));

        Assert.All(outcomes, o => Assert.Equal(DropResult.Returned, Assert.Single(o).Result));
        Assert.Equal(5u, (uint)f.Operations.AmountOf(RedPotion)); // returned once, not twice
        Assert.Single(f.Operations.Applied, r => r.ResolvesPendingDrop != Guid.Empty);
    }

    [Fact]
    public async Task WorldRestartBeforeConfirmation_IsSettledAsLostNeverDuplicated_AndReported()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        for (var i = 0; i < 3; i++) f.World.DropFaults.Enqueue(DropFault.LostBeforeApplied);
        f.World.ResolveOverride = WorldGroundItemDropResolutionStatus.EpochChanged;

        Assert.Equal(DropResult.LostToWorldRestart, (await f.Service.DropAsync(0, 2, Map, 150, 180, Items, CancellationToken.None)).Result);
        Assert.Equal(3u, Units(f, RedPotion));
        Assert.Empty(f.Operations.PendingDrops);
    }
}
