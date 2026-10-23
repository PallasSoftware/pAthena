using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.Shared.Inventory;
using MapServer.Tests.Testing;

namespace Athena.Net.MapServer.Tests.World;

// The session-side inventory service over the CharServer stand-in (the REAL shared operation rules): grants and removals are planned from
// the cached rows, made durable atomically and idempotently, and only then reflected in the snapshot.
public sealed class CharacterInventoryServiceTests
{
    private const uint AccountId = 1;
    private const uint CharId = 100;

    private static readonly ItemDefinition Wood = new EtcItemDefinition(6008, "Wood", "Wood", Stackable: true, ClientViewId: 6008, new("rAthena", "abc", "db/re/item_db_etc.yml", 1));
    private static readonly ItemDefinition Knife = new WeaponItemDefinition(1201, "Knife", "Knife", Stackable: false, ClientViewId: 1201, Attack: 17, WeaponLevel: 1, WeaponType.Dagger, Range: 1, EquipLocation: 0x2, new("rAthena", "abc", "db/re/item_db_equip.yml", 1));
    private static readonly ItemDefinition Potion = new HealingItemDefinition(501, "Red_Potion", "Red Potion", Stackable: true, ClientViewId: 501, new("rAthena", "abc", "db/re/item_db_usable.yml", 1));

    private static (InMemoryInventoryOperations Operations, CharacterInventoryService Service) Setup(params CharacterInventoryItem[] rows)
    {
        var snapshot = new CharacterInventorySnapshot(rows);
        var operations = new InMemoryInventoryOperations(snapshot);
        return (operations, global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, snapshot));
    }

    private static CharacterInventoryItem Row(uint id, uint slot, int itemId, uint amount, uint equip = 0, ItemInstanceDetail? detail = null) =>
        new(id, slot, itemId, amount, equip, true, 0, 0, 0, detail);

    [Fact]
    public async Task Grant_FirstAward_CreatesANewStack_SecondExtendsIt()
    {
        var (operations, service) = Setup();

        var first = await service.GrantAsync(Wood, 3, CancellationToken.None);
        var second = await service.GrantAsync(Wood, 2, CancellationToken.None);

        Assert.True(first.Succeeded && second.Succeeded);
        var row = Assert.Single(service.Snapshot.Items);
        Assert.Equal((6008, 5u, 0u), (row.ItemId, row.Amount, row.SlotIndex));
        Assert.IsType<AddNewRowMutation>(Assert.Single(first.Mutations));
        Assert.IsType<AddToStackMutation>(Assert.Single(second.Mutations));
        Assert.Equal(5, operations.AmountOf(6008));
        Assert.Equal([(row, 2u)], second.Placements());
    }

    [Fact]
    public async Task Grant_NonStackableItems_GetOneRowEach()
    {
        var (operations, service) = Setup();

        var change = await service.GrantAsync(Knife, 3, CancellationToken.None);

        Assert.True(change.Succeeded);
        Assert.Equal([1u, 1u, 1u], service.Snapshot.Items.Select(row => row.Amount));
        Assert.Equal(3, operations.Rows.Count);
        Assert.Equal([0u, 1u, 2u], service.Snapshot.Items.Select(row => row.SlotIndex));
    }

    // Pinned pc_additem never splits a stack: an add that would pass MAX_AMOUNT (30000) is ADDITEM_OVERAMOUNT and changes nothing.
    [Fact]
    public async Task Grant_ExceedingTheStackLimit_IsRejected_NothingIsSent()
    {
        var (operations, service) = Setup(Row(1, 0, 6008, 29_990));

        var change = await service.GrantAsync(Wood, 25, CancellationToken.None);

        Assert.Equal(InventoryChangeStatus.Rejected, change.Status);
        Assert.Empty(operations.Requests);
        Assert.Equal(29_990, operations.AmountOf(6008));
    }

    [Fact]
    public async Task Grant_DoesNotMergeIntoBoundRentalOrEquippedRows()
    {
        var rental = new ItemInstanceDetail(0, 0, 0, 0, 0, 0, default, default, default, default, default, ExpireTime: 1_900_000_000, 0, 0, 0);
        var (_, service) = Setup(Row(1, 0, 501, 5, detail: rental), new CharacterInventoryItem(2, 1, 501, 5, 0, true, 0, 0, Bound: 1));

        var change = await service.GrantAsync(Potion, 1, CancellationToken.None);

        Assert.IsType<AddNewRowMutation>(Assert.Single(change.Mutations));
        Assert.Equal(3, service.Snapshot.Items.Count);
    }

    [Fact]
    public async Task Grant_SeveralItemsAtOnce_AreOneAtomicOperation_InTheOrderGiven()
    {
        var (operations, service) = Setup(Row(1, 0, 6008, 1));

        var change = await service.GrantAsync([(Potion, 10u), (Wood, 4u), (Knife, 1u)], InventoryOperationKind.Add, CancellationToken.None);

        Assert.True(change.Succeeded);
        Assert.Single(operations.Applied);
        Assert.Equal([501, 6008, 1201], change.Placements().Select(p => p.Row.ItemId));
        Assert.Equal(5u, service.Snapshot.Items.Single(i => i.ItemId == 6008).Amount);
    }

    [Fact]
    public async Task RemoveByItemId_ConsumesAcrossRowsInDurableIdOrder_Atomically()
    {
        var (operations, service) = Setup(Row(2, 1, 6008, 3), Row(1, 0, 6008, 2));

        var change = await service.RemoveByItemIdAsync(6008, 4, CancellationToken.None);

        Assert.True(change.Succeeded);
        Assert.Equal([(1u, 2u), (2u, 2u)], change.Mutations.Cast<RemoveFromRowMutation>().Select(m => (m.DurableId, m.Amount)));
        Assert.Single(operations.Applied);
        var left = Assert.Single(service.Snapshot.Items);
        Assert.Equal((2u, 1u), (left.DurableId, left.Amount));
    }

    [Fact]
    public async Task RemoveByItemId_InsufficientOrEquipped_IsRejectedWithoutSendingAnything()
    {
        var (operations, service) = Setup(Row(1, 0, 6008, 2), Row(2, 1, 1201, 1, equip: 0x2));

        Assert.Equal(InventoryChangeStatus.Rejected, (await service.RemoveByItemIdAsync(6008, 3, CancellationToken.None)).Status);
        Assert.Equal(InventoryChangeStatus.Rejected, (await service.RemoveByItemIdAsync(1201, 1, CancellationToken.None)).Status);
        Assert.Empty(operations.Requests);
    }

    [Fact]
    public async Task LostAcknowledgement_IsResentWithTheSameId_AndAppliedOnce()
    {
        var (operations, service) = Setup();
        operations.LoseAnswers = 1;

        var change = await service.GrantAsync(Wood, 7, CancellationToken.None);

        Assert.True(change.Succeeded);
        Assert.Equal(2, operations.Requests.Count);
        Assert.Equal(operations.Requests[0].OperationId, operations.Requests[1].OperationId);
        Assert.Single(operations.Applied);
        Assert.Equal(7, operations.AmountOf(6008));              // not 14
        Assert.Equal(7u, service.Snapshot.Items.Single().Amount);
    }

    [Fact]
    public async Task UnreachableCharServer_LeavesTheChangeInDoubt_ThenResolvesToTheTruth()
    {
        var (operations, service) = Setup();
        operations.LoseAnswers = 3; // applied, but every attempt's answer is lost

        var change = await service.GrantAsync(Wood, 7, CancellationToken.None);

        Assert.Equal(InventoryChangeStatus.InDoubt, change.Status);
        Assert.Empty(service.Snapshot.Items);                      // the cache is not guessed
        Assert.Equal(7, operations.AmountOf(6008));                // ...even though the change is durable
        Assert.Equal([change.OperationId], service.InDoubtOperations);

        var resolved = await service.ResolveAsync(change.OperationId, CancellationToken.None);

        Assert.True(resolved.Succeeded);
        Assert.Equal(7, operations.AmountOf(6008));                // still once
        Assert.Equal(7u, service.Snapshot.Items.Single().Amount);
        Assert.Empty(service.InDoubtOperations);
    }

    [Fact]
    public async Task NeverAppliedAndUnreachable_ResolvesToAppliedOnceReachable_Never_Twice()
    {
        var (operations, service) = Setup();
        operations.DropRequests = 3;

        var change = await service.GrantAsync(Wood, 7, CancellationToken.None);
        Assert.Equal(InventoryChangeStatus.InDoubt, change.Status);
        Assert.Equal(0, operations.AmountOf(6008));

        var resolved = await service.ResolveAsync(change.OperationId, CancellationToken.None);

        Assert.True(resolved.Succeeded);
        Assert.Equal(7, operations.AmountOf(6008));
    }

    [Fact]
    public async Task InDoubt_ForcesAReloadBeforeTheNextPlan()
    {
        var (operations, service) = Setup(Row(1, 0, 6008, 1));
        operations.DropRequests = 3;
        var doubtful = await service.GrantAsync(Wood, 1, CancellationToken.None);
        Assert.Equal(InventoryChangeStatus.InDoubt, doubtful.Status);

        var next = await service.GrantAsync(Wood, 1, CancellationToken.None);

        Assert.True(next.Succeeded);
        Assert.Equal(2, operations.AmountOf(6008)); // the in-doubt grant was never applied; the new one was
        Assert.Equal(2u, service.Snapshot.Items.Single().Amount);
    }

    [Fact]
    public async Task StaleCache_IsRejectedByRevision_ThenReloaded()
    {
        var (operations, service) = Setup(Row(1, 0, 6008, 5));
        // Someone else (e.g. a second session on another replica) changed the row.
        var other = global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, service.Snapshot);
        await other.GrantAsync(Wood, 10, CancellationToken.None);

        var change = await service.GrantAsync(Wood, 1, CancellationToken.None);

        Assert.Equal(InventoryChangeStatus.Conflict, change.Status);
        Assert.Equal(15, operations.AmountOf(6008));            // the stale write did not apply
        Assert.Equal(15u, service.Snapshot.Items.Single().Amount); // the cache was reloaded
    }

    [Fact]
    public async Task ReloadFailure_AfterAConflict_KeepsTheServiceFailingClosedUntilItCanReload()
    {
        var (operations, service) = Setup(Row(1, 0, 6008, 5));
        var other = global::MapServer.Tests.Testing.TestInventory.Service(AccountId, CharId, operations, service.Snapshot);
        await other.GrantAsync(Wood, 10, CancellationToken.None);
        operations.FailReads = true;

        Assert.Equal(InventoryChangeStatus.Conflict, (await service.GrantAsync(Wood, 1, CancellationToken.None)).Status);
        Assert.Equal(InventoryChangeStatus.Unavailable, (await service.GrantAsync(Wood, 1, CancellationToken.None)).Status);

        operations.FailReads = false;
        Assert.True((await service.GrantAsync(Wood, 1, CancellationToken.None)).Succeeded);
        Assert.Equal(16, operations.AmountOf(6008));
    }

    [Fact]
    public async Task ConcurrentGrants_AreSerialized_AndNoneIsLost()
    {
        var (operations, service) = Setup();

        var changes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GrantAsync(Wood, 1, CancellationToken.None)));

        Assert.All(changes, change => Assert.True(change.Succeeded));
        Assert.Equal(20, operations.AmountOf(6008));
        Assert.Equal(20u, service.Snapshot.Items.Single().Amount);
    }

    [Fact]
    public async Task DeletedRows_LeaveHoles_ThatTheNextGrantReuses()
    {
        var (_, service) = Setup(Row(1, 0, 1201, 1), Row(2, 1, 6008, 1), Row(3, 2, 501, 1));

        await service.RemoveByItemIdAsync(6008, 1, CancellationToken.None);   // hole at slot 1
        var change = await service.GrantAsync(Knife, 1, CancellationToken.None);

        Assert.Equal(1u, Assert.Single(change.Placements()).Row.SlotIndex);
        Assert.Equal(2u, service.Snapshot.Items.Single(i => i.ItemId == 501).SlotIndex); // untouched
    }
}
