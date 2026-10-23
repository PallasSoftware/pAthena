using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.Shared.Inventory;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;
using MapServer.Tests.Testing;
using Orleans.TestingHost;

namespace Athena.Net.MapServer.Tests.World;

// Player item drop (pinned pc_dropitem / pc_candrop) across both authorities: the REAL World grain on a TestCluster for
// the ground item and CharServer's real operation rules (InMemoryInventoryOperations) for the durable row.
public sealed class CharacterItemDropServiceTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<GroundItemCrossServerIntegrationTests.TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items = ItemCatalog.Current.ById;
    private const int RedPotion = 501, Knife = 1201, ThiefBugCard = 4016;

    private IWorldRuntime Runtime() => new OrleansWorldRuntime(_cluster.Client,
        WorldPartitionTopologyLoader.Load(Path.Combine(RepositoryRoot(), "conf", "world_partitions.json"), ["prontera", "prt_fild08d", "izlude", "geffen"]));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static CharacterInventoryItem Row(uint id, int itemId, uint amount = 1, uint equip = 0, byte refine = 0, byte bound = 0, uint equipSwitch = 0, uint expire = 0, uint card0 = 0) =>
        new(id, id - 1, itemId, amount, equip, true, refine, 0, bound, ItemInstanceDetail.Empty with { Card0 = card0, EquipSwitch = equipSwitch, ExpireTime = expire, Revision = 1 });

    private (InMemoryInventoryOperations Operations, CharacterInventoryService Inventory, CharacterItemDropService Service, IWorldRuntime World) Setup(params CharacterInventoryItem[] rows)
    {
        var snapshot = new CharacterInventorySnapshot(rows);
        var operations = new InMemoryInventoryOperations(snapshot);
        var inventory = TestInventory.Service(7, 9, operations, snapshot);
        var world = Runtime();
        return (operations, inventory, new CharacterItemDropService(inventory, world), world);
    }

    [Fact]
    public async Task DropPartOfAStack_RemovesThoseUnitsDurably_AndPutsExactlyThemOnTheGround()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));

        var (result, groundItemId) = await f.Service.DropAsync(0, 3, "prontera", 150, 180, Items, CancellationToken.None);

        Assert.Equal(CharacterItemDropService.DropResult.Dropped, result);
        Assert.Equal(2u, f.Operations.Rows.Single().Amount);
        var ground = Assert.Single((await f.World.GetGroundItemsAsync("prontera", CancellationToken.None)).Items);
        Assert.Equal((groundItemId!.Value, RedPotion, 3u, (ushort)150, (ushort)180), (ground.GroundItemId, ground.Content.ItemId, ground.Content.Amount, ground.X, ground.Y));
        Assert.Equal(InventoryOperationKind.Drop, f.Operations.Applied.Single().Kind);
    }

    [Fact]
    public async Task DroppedInstance_IsGrantedBackIntact_ToWhoeverPicksItUp()
    {
        // A +7 Knife carrying Thief Bug Card: pinned map_addflooritem copies the whole struct item.
        var f = Setup(Row(1, Knife, refine: 7, card0: ThiefBugCard));
        var (_, groundItemId) = await f.Service.DropAsync(0, 1, "prontera", 150, 180, Items, CancellationToken.None);

        var picker = TestInventory.Service(8, 10, new InMemoryInventoryOperations(new CharacterInventorySnapshot([])), new CharacterInventorySnapshot([]));
        var reference = new WorldGroundItemReference("prontera", (await f.World.GetGroundItemsAsync("prontera", CancellationToken.None)).Epoch, groundItemId!.Value);
        var claim = await f.World.ClaimGroundItemAsync(new WorldGroundItemClaim(reference, 10, Guid.NewGuid()), CancellationToken.None);
        var grant = await picker.GrantAsync([new InventoryPlanner.GrantRequest(Items[Knife], 1, CharacterItemDropService.PickupTemplate(claim.Content!))], InventoryOperationKind.Pickup, CancellationToken.None);

        var picked = Assert.Single(grant.Snapshot.Items);
        Assert.Equal((Knife, (byte)7, (uint)ThiefBugCard, 0u, 0u), (picked.ItemId, picked.Refine, picked.Details.Card0, picked.Equip, picked.Details.EquipSwitch));
    }

    public static TheoryData<string> Refusals => new() { "equipped", "switch", "bound", "rental", "nodrop", "amount", "unknown" };

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
        var slot = refusal == "unknown" ? 9u : 0u;
        var amount = refusal == "amount" ? 3u : 1u;

        var (result, _) = await f.Service.DropAsync(slot, amount, "prontera", 150, 180, Items, CancellationToken.None);

        Assert.NotEqual(CharacterItemDropService.DropResult.Dropped, result);
        Assert.Empty(f.Operations.Requests);
        Assert.Empty((await f.World.GetGroundItemsAsync("prontera", CancellationToken.None)).Items);
    }

    [Fact]
    public async Task LostAcknowledgementOfTheRemoval_IsReplayed_AndDropsOnce()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        f.Operations.LoseAnswers = 1;

        var (result, _) = await f.Service.DropAsync(0, 2, "prontera", 150, 180, Items, CancellationToken.None);

        Assert.Equal(CharacterItemDropService.DropResult.Dropped, result);
        Assert.Single(f.Operations.Applied);
        Assert.Equal(3u, f.Operations.Rows.Single().Amount);
        Assert.Single((await f.World.GetGroundItemsAsync("prontera", CancellationToken.None)).Items);
    }

    [Fact]
    public async Task RemovalThatCannotBePersisted_NeverReachesTheGround()
    {
        var f = Setup(Row(1, RedPotion, amount: 5));
        f.Operations.DropRequests = 3;

        var (result, _) = await f.Service.DropAsync(0, 2, "prontera", 150, 180, Items, CancellationToken.None);

        Assert.Equal(CharacterItemDropService.DropResult.PersistenceFailed, result);
        Assert.Equal(5u, f.Operations.Rows.Single().Amount);
        Assert.Empty((await f.World.GetGroundItemsAsync("prontera", CancellationToken.None)).Items);
    }

    // The real runtime for everything except DropGroundItemAsync, which World answers with a definitive MapNotFound.
    public class RefusingDropProxy : System.Reflection.DispatchProxy
    {
        public IWorldRuntime Inner { get; set; } = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IWorldRuntime.DropGroundItemAsync)
                ? Task.FromResult(new WorldGroundItemDropResult(WorldGroundItemDropStatus.MapNotFound, null))
                : method.Invoke(Inner, args);
    }

    [Fact]
    public async Task WorldRefusingTheDrop_GivesTheUnitsBackOnce()
    {
        var f = Setup(Row(1, Knife, refine: 7, card0: ThiefBugCard));
        var refusing = System.Reflection.DispatchProxy.Create<IWorldRuntime, RefusingDropProxy>();
        ((RefusingDropProxy)(object)refusing).Inner = f.World;
        var service = new CharacterItemDropService(f.Inventory, refusing);

        var (result, _) = await service.DropAsync(0, 1, "prontera", 150, 180, Items, CancellationToken.None);

        Assert.Equal(CharacterItemDropService.DropResult.Refused, result);
        var restored = Assert.Single(f.Operations.Rows);
        Assert.Equal((Knife, (byte)7, (uint)ThiefBugCard), ((int)restored.ItemId, restored.Refine, restored.Card0));
        Assert.Equal(2, f.Operations.Applied.Count); // the removal and its one compensation
    }
}
