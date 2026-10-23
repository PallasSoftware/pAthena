using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Inventory;
using Athena.Net.CharServer.Tests.Db;
using Athena.Net.Shared.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.CharServer.Tests.Inventory;

/// <summary>
/// The durability contract of the inventory channel against a real SQL Server: atomic batches, idempotent replays under lost
/// acknowledgements and concurrent duplicates, and rejection of stale writers.
/// </summary>
public sealed class InventoryOperationExecutorTests
{
    private const uint AccountId = 2_000_001;

    private sealed class Fixture : IAsyncDisposable
    {
        internal required SqlServerTestDatabase Database { get; init; }
        internal required InventoryOperationExecutor Executor { get; init; }
        internal uint CharId { get; init; }
        internal uint OtherCharId { get; init; }

        internal InventoryOperationRequest Op(Guid id, params InventoryMutation[] mutations) => new(AccountId, CharId, id, InventoryOperationKind.Add, mutations);
        internal InventoryOperationRequest Op(params InventoryMutation[] mutations) => Op(Guid.NewGuid(), mutations);
        internal InventoryOperationRequest RefineOp(uint zenyCost, uint expectedZeny, params InventoryMutation[] mutations) =>
            new(AccountId, CharId, Guid.NewGuid(), InventoryOperationKind.Refine, mutations, zenyCost, expectedZeny);

        internal static async Task<Fixture> CreateAsync(uint guildId = 0, uint zeny = 0)
        {
            var database = await SqlServerTestDatabase.CreateAsync();
            var alpha = new CharCharacter { Name = "Alpha", AccountId = AccountId, GuildId = guildId, Zeny = zeny };
            var beta = new CharCharacter { Name = "Beta", AccountId = AccountId + 1 };
            await using (var db = database.NewContext())
            {
                db.Characters.AddRange(alpha, beta);
                await db.SaveChangesAsync();
            }
            return new Fixture { Database = database, CharId = alpha.CharId, OtherCharId = beta.CharId, Executor = new InventoryOperationExecutor(() => database.NewContext()) };
        }

        internal async Task<InventoryOperationResponse> RunAsync(InventoryOperationRequest request)
        {
            var bytes = InventoryOperationWire.BuildRequest(request);
            Assert.True(InventoryOperationWire.TryParseRequest(bytes, out var parsed));
            return await Executor.ExecuteAsync(parsed, bytes, CancellationToken.None);
        }

        internal async Task<List<CharInventory>> InventoryAsync(uint? charId = null)
        {
            await using var db = Database.NewContext();
            return await db.Inventory.AsNoTracking().Where(i => i.CharId == (charId ?? CharId)).OrderBy(i => i.Id).ToListAsync();
        }

        internal async Task<uint> ZenyAsync()
        {
            await using var db = Database.NewContext();
            return await db.Characters.AsNoTracking().Where(c => c.CharId == CharId).Select(c => c.Zeny).SingleAsync();
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private static AddNewRowMutation AddNew(uint itemId = 501, uint amount = 3, InventoryContainer container = InventoryContainer.Inventory) =>
        new(container, InventoryItemRow.New(itemId, amount));

    [SqlServerFact]
    public async Task AddNew_PersistsEveryColumn_AndReturnsTheRowWithItsRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var template = InventoryItemRow.New(1201, 1) with
        {
            Refine = 7, Attribute = 1, Card0 = 4001, Card1 = 4002, Card2 = 0, Card3 = 4003, Bound = 2, Favorite = 1, EnchantGrade = 3,
            Option1 = new InventoryOption(12, 345, 6), Option4 = new InventoryOption(-9, -1, 255), ExpireTime = 1_900_000_000, UniqueId = 0xAABBCCDD11223344, EquipSwitch = 0x20,
        };

        var response = await fixture.RunAsync(fixture.Op(new AddNewRowMutation(InventoryContainer.Inventory, template)));

        Assert.Equal(InventoryOperationStatus.Applied, response.Status);
        var result = Assert.Single(response.Results);
        Assert.NotEqual(0u, result.DurableId);
        Assert.Equal(template with { DurableId = result.DurableId, Revision = 1 }, result.Row);

        var stored = Assert.Single(await fixture.InventoryAsync());
        Assert.Equal((1201u, 7, 1, 4001u, 4003u, 2, 1, 3), (stored.NameId, (int)stored.Refine, (int)stored.Attribute, stored.Card0, stored.Card3, (int)stored.Bound, (int)stored.Favorite, (int)stored.EnchantGrade));
        Assert.Equal((12, 345, 6, -9, -1, 255), (stored.OptionId1, stored.OptionVal1, (int)stored.OptionParm1, stored.OptionId4, stored.OptionVal4, (int)stored.OptionParm4));
        Assert.Equal((1_900_000_000u, 0xAABBCCDD11223344UL, 0x20u, 1u), (stored.ExpireTime, stored.UniqueId, stored.EquipSwitch, stored.Revision));
    }

    // An equip switch (MapServer CharacterEquipmentMutationService.SwitchAllAsync) is one EquipSwitch-kind batch that moves
    // Equip and EquipSwitch between rows. Real SQL Server: the swap commits together, a stale row rejects all of it, and a
    // lost acknowledgement replays instead of swapping back.
    [SqlServerFact]
    public async Task EquipSwitchSwap_IsAtomic_RejectsAStaleRowEntirely_AndReplaysALostAck()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.RunAsync(fixture.Op(
            new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(1201, 1) with { Equip = 0x2 }),
            new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(1201, 1) with { EquipSwitch = 0x2 })));
        var (worn, registered) = (seeded.Results[0].Row!, seeded.Results[1].Row!);
        InventoryOperationRequest Swap(uint registeredRevision) => new(AccountId, fixture.CharId, Guid.NewGuid(), InventoryOperationKind.EquipSwitch,
        [
            new UpdateRowMutation(InventoryContainer.Inventory, worn.DurableId, worn.Revision, worn with { Equip = 0, EquipSwitch = 0x2 }),
            new UpdateRowMutation(InventoryContainer.Inventory, registered.DurableId, registeredRevision, registered with { Equip = 0x2, EquipSwitch = 0 }),
        ]);

        var stale = await fixture.RunAsync(Swap(registered.Revision + 7));
        Assert.Equal(InventoryOperationStatus.PreconditionFailed, stale.Status);
        Assert.Equal([(0x2u, 0u), (0u, 0x2u)], (await fixture.InventoryAsync()).Select(r => (r.Equip, r.EquipSwitch)));

        var swap = Swap(registered.Revision);
        Assert.Equal(InventoryOperationStatus.Applied, (await fixture.RunAsync(swap)).Status);
        Assert.Equal(InventoryOperationStatus.Replayed, (await fixture.RunAsync(swap)).Status);
        Assert.Equal([(0u, 0x2u), (0x2u, 0u)], (await fixture.InventoryAsync()).Select(r => (r.Equip, r.EquipSwitch)));
    }

    [SqlServerFact]
    public async Task RepeatedOperationId_IsReplayedFromTheLog_AndAppliesNothingTwice()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Op(AddNew(501, 5));

        var first = await fixture.RunAsync(request);
        var lostAckRetry = await fixture.RunAsync(request);

        Assert.Equal(InventoryOperationStatus.Applied, first.Status);
        Assert.Equal(InventoryOperationStatus.Replayed, lostAckRetry.Status);
        Assert.Equal(first.Results, lostAckRetry.Results);
        Assert.Single(await fixture.InventoryAsync());
    }

    [SqlServerFact]
    public async Task OperationIdReusedWithDifferentPayload_IsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        Assert.Equal(InventoryOperationStatus.Applied, (await fixture.RunAsync(fixture.Op(id, AddNew(501, 5)))).Status);

        var reused = await fixture.RunAsync(fixture.Op(id, AddNew(502, 9)));

        Assert.Equal(InventoryOperationStatus.Invalid, reused.Status);
        Assert.Equal(5u, Assert.Single(await fixture.InventoryAsync()).Amount);
    }

    [SqlServerFact]
    public async Task ConcurrentDuplicateOperations_ApplyExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Op(AddNew(501, 1));

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => fixture.RunAsync(request))));

        Assert.Equal(1, responses.Count(r => r.Status == InventoryOperationStatus.Applied));
        Assert.All(responses.Where(r => r.Status != InventoryOperationStatus.Applied), r => Assert.Equal(InventoryOperationStatus.Replayed, r.Status));
        Assert.Single(await fixture.InventoryAsync());
    }

    [SqlServerFact]
    public async Task FailedPrecondition_RollsBackTheWholeBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = (await fixture.RunAsync(fixture.Op(AddNew(501, 10)))).Results.Single().Row!;

        var response = await fixture.RunAsync(fixture.Op(
            AddNew(502, 1),
            new RemoveFromRowMutation(InventoryContainer.Inventory, seeded.DurableId, 4, ExpectedAmount: 99, seeded.Revision)));

        Assert.Equal(InventoryOperationStatus.PreconditionFailed, response.Status);
        Assert.Equal(1, response.FailedIndex);
        var rows = await fixture.InventoryAsync();
        Assert.Equal(501u, Assert.Single(rows).NameId);
        Assert.Equal(10u, rows[0].Amount);
    }

    [SqlServerFact]
    public async Task Remove_ConsumesPartially_ThenDeletesTheRowAtZero()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = (await fixture.RunAsync(fixture.Op(AddNew(501, 10)))).Results.Single().Row!;

        var partial = await fixture.RunAsync(fixture.Op(new RemoveFromRowMutation(InventoryContainer.Inventory, seeded.DurableId, 4, 10, seeded.Revision)));
        var remaining = partial.Results.Single().Row!;
        Assert.Equal((6u, seeded.Revision + 1), (remaining.Amount, remaining.Revision));

        var final = await fixture.RunAsync(fixture.Op(new RemoveFromRowMutation(InventoryContainer.Inventory, seeded.DurableId, 6, 6, remaining.Revision)));
        var deleted = final.Results.Single();
        Assert.True(deleted.Deleted);
        Assert.Null(deleted.Row);
        Assert.Empty(await fixture.InventoryAsync());
    }

    [SqlServerFact]
    public async Task AddToStack_RespectsTheNamedRowTheCapAndTheRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = (await fixture.RunAsync(fixture.Op(AddNew(501, 20)))).Results.Single().Row!;

        var overCap = await fixture.RunAsync(fixture.Op(new AddToStackMutation(InventoryContainer.Inventory, seeded.DurableId, 501, 11, 20, MaxAmount: 30, seeded.Revision)));
        Assert.Equal(InventoryOperationStatus.PreconditionFailed, overCap.Status);

        var wrongItem = await fixture.RunAsync(fixture.Op(new AddToStackMutation(InventoryContainer.Inventory, seeded.DurableId, 502, 1, 20, 30, seeded.Revision)));
        Assert.Equal(InventoryOperationStatus.PreconditionFailed, wrongItem.Status);

        var ok = await fixture.RunAsync(fixture.Op(new AddToStackMutation(InventoryContainer.Inventory, seeded.DurableId, 501, 10, 20, 30, seeded.Revision)));
        Assert.Equal(30u, ok.Results.Single().Row!.Amount);
        Assert.Equal(30u, Assert.Single(await fixture.InventoryAsync()).Amount);
    }

    [SqlServerFact]
    public async Task Update_ReplacesMutableColumns_AndRejectsAStaleRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = (await fixture.RunAsync(fixture.Op(AddNew(1201, 1)))).Results.Single().Row!;

        var refined = await fixture.RunAsync(fixture.Op(new UpdateRowMutation(InventoryContainer.Inventory, seeded.DurableId, seeded.Revision, seeded with { Refine = 9, Card0 = 4001, Equip = 2 })));
        Assert.Equal(InventoryOperationStatus.Applied, refined.Status);
        Assert.Equal((9, 4001u, 2u, seeded.Revision + 1), ((int)refined.Results.Single().Row!.Refine, refined.Results.Single().Row!.Card0, refined.Results.Single().Row!.Equip, refined.Results.Single().Row!.Revision));

        var stale = await fixture.RunAsync(fixture.Op(new UpdateRowMutation(InventoryContainer.Inventory, seeded.DurableId, seeded.Revision, seeded with { Refine = 1 })));
        Assert.Equal(InventoryOperationStatus.PreconditionFailed, stale.Status);
        Assert.Equal(9, Assert.Single(await fixture.InventoryAsync()).Refine);
    }

    [SqlServerFact]
    public async Task CompetingWritersWithTheSameRevision_OnlyOneWins()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = (await fixture.RunAsync(fixture.Op(AddNew(501, 10)))).Results.Single().Row!;

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            fixture.RunAsync(fixture.Op(new RemoveFromRowMutation(InventoryContainer.Inventory, seeded.DurableId, 10, 10, seeded.Revision))))));

        Assert.Equal(1, responses.Count(r => r.Status == InventoryOperationStatus.Applied));
        Assert.Equal(7, responses.Count(r => r.Status == InventoryOperationStatus.PreconditionFailed));
        Assert.Empty(await fixture.InventoryAsync());
    }

    [SqlServerFact]
    public async Task OtherCharactersRows_CannotBeMutated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = await fixture.RunAsync(new InventoryOperationRequest(AccountId + 1, fixture.OtherCharId, Guid.NewGuid(), InventoryOperationKind.Add, [AddNew(501, 5)]));
        var foreignRow = other.Results.Single().Row!;

        var attack = await fixture.RunAsync(fixture.Op(new RemoveFromRowMutation(InventoryContainer.Inventory, foreignRow.DurableId, 5, 5, foreignRow.Revision)));

        Assert.Equal(InventoryOperationStatus.PreconditionFailed, attack.Status);
        Assert.Single(await fixture.InventoryAsync(fixture.OtherCharId));
    }

    [SqlServerFact]
    public async Task Containers_AreOwnedByCharacterAccountAndGuild()
    {
        await using var fixture = await Fixture.CreateAsync(guildId: 77);

        var response = await fixture.RunAsync(fixture.Op(
            AddNew(501, 1, InventoryContainer.Cart), AddNew(502, 2, InventoryContainer.Storage), AddNew(503, 3, InventoryContainer.GuildStorage)));

        Assert.Equal(InventoryOperationStatus.Applied, response.Status);
        await using var db = fixture.Database.NewContext();
        var rows = await db.ContainerItems.AsNoTracking().OrderBy(r => r.Container).ToListAsync();
        Assert.Equal([(1, fixture.CharId), (2, AccountId), (3, 77u)], rows.Select(r => ((int)r.Container, r.OwnerId)).ToArray());
    }

    [SqlServerFact]
    public async Task GuildStorage_WithoutAGuild_IsRejected()
    {
        await using var fixture = await Fixture.CreateAsync(guildId: 0);

        var response = await fixture.RunAsync(fixture.Op(AddNew(501, 1, InventoryContainer.GuildStorage)));

        Assert.Equal(InventoryOperationStatus.Invalid, response.Status);
    }

    [SqlServerFact]
    public async Task MalformedOperations_AreInvalid_AndApplyNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = (await fixture.RunAsync(fixture.Op(AddNew(501, 10)))).Results.Single().Row!;

        var sameRowTwice = await fixture.RunAsync(fixture.Op(
            new RemoveFromRowMutation(InventoryContainer.Inventory, seeded.DurableId, 1, 10, seeded.Revision),
            new RemoveFromRowMutation(InventoryContainer.Inventory, seeded.DurableId, 1, 10, seeded.Revision)));
        var zeroAmount = await fixture.RunAsync(fixture.Op(AddNew(501, 0)));

        Assert.Equal(InventoryOperationStatus.Invalid, sameRowTwice.Status);
        Assert.Equal(InventoryOperationStatus.Invalid, zeroAmount.Status);
        Assert.Equal(10u, Assert.Single(await fixture.InventoryAsync()).Amount);
    }

    [SqlServerFact]
    public async Task DatabaseOutage_IsUnavailable_AndTheSameOperationIdCanBeRetriedSafely()
    {
        await using var fixture = await Fixture.CreateAsync();
        var down = new InventoryOperationExecutor(() => null);
        var request = fixture.Op(AddNew(501, 2));
        var bytes = InventoryOperationWire.BuildRequest(request);
        Assert.True(InventoryOperationWire.TryParseRequest(bytes, out var parsed));

        var outage = await down.ExecuteAsync(parsed, bytes, CancellationToken.None);
        Assert.Equal(InventoryOperationStatus.Unavailable, outage.Status);
        Assert.Empty(await fixture.InventoryAsync());

        var retry = await fixture.RunAsync(request);
        Assert.Equal(InventoryOperationStatus.Applied, retry.Status);
        Assert.Single(await fixture.InventoryAsync());
    }

    // Zeny extension (Feature 16 S6 refine-action mutation): ZenyCost/ExpectedZeny ride the SAME transaction and
    // the SAME OperationId-keyed idempotency log as every other inventory operation - no second currency
    // transaction, no new persistence mechanism.

    [SqlServerFact]
    public async Task ZenyCost_DeductsAtomicallyWithTheRowMutations()
    {
        await using var fixture = await Fixture.CreateAsync(zeny: 1000);
        var material = (await fixture.RunAsync(fixture.Op(AddNew(1010, 1)))).Results.Single().Row!; // one Phracon.

        var response = await fixture.RunAsync(fixture.RefineOp(zenyCost: 50, expectedZeny: 1000,
            new RemoveFromRowMutation(InventoryContainer.Inventory, material.DurableId, 1, 1, material.Revision)));

        Assert.Equal(InventoryOperationStatus.Applied, response.Status);
        Assert.Equal(950u, response.RemainingZeny);
        Assert.Equal(950u, await fixture.ZenyAsync());
        Assert.Empty(await fixture.InventoryAsync()); // the material was consumed in the SAME commit.
    }

    [SqlServerFact]
    public async Task ZenyCost_InsufficientFunds_RejectsTheWholeOperation_NothingPersisted()
    {
        await using var fixture = await Fixture.CreateAsync(zeny: 40);
        var material = (await fixture.RunAsync(fixture.Op(AddNew(1010, 1)))).Results.Single().Row!;

        var response = await fixture.RunAsync(fixture.RefineOp(zenyCost: 50, expectedZeny: 40,
            new RemoveFromRowMutation(InventoryContainer.Inventory, material.DurableId, 1, 1, material.Revision)));

        Assert.Equal(InventoryOperationStatus.PreconditionFailed, response.Status);
        Assert.Equal(-1, response.FailedIndex); // a currency failure names no single mutation index.
        Assert.Equal(40u, await fixture.ZenyAsync()); // untouched.
        Assert.Equal(1u, Assert.Single(await fixture.InventoryAsync()).Amount); // material NOT consumed.
    }

    [SqlServerFact]
    public async Task ZenyCost_StaleExpectedZeny_IsRejectedLikeAnyStaleWrite()
    {
        await using var fixture = await Fixture.CreateAsync(zeny: 1000);
        await fixture.RunAsync(fixture.RefineOp(zenyCost: 100, expectedZeny: 1000, AddNew(1010, 1))); // spends down to 900.

        // Retries with the ORIGINAL (now stale) balance of 1000, not the current 900.
        var stale = await fixture.RunAsync(fixture.RefineOp(zenyCost: 50, expectedZeny: 1000, AddNew(1011, 1)));

        Assert.Equal(InventoryOperationStatus.PreconditionFailed, stale.Status);
        Assert.Equal(900u, await fixture.ZenyAsync());
        Assert.Single(await fixture.InventoryAsync()); // the second AddNew never persisted.
    }

    [SqlServerFact]
    public async Task ZenyCost_ConcurrentRequests_OnlyOneSucceeds_NoDoubleCharge()
    {
        await using var fixture = await Fixture.CreateAsync(zeny: 100);

        // Eight concurrent attempts, each independently reading the same starting balance (100) and spending 100 -
        // only one can legitimately succeed; every other must see the ALREADY-DECREMENTED balance and fail,
        // never stack multiple deductions past zero.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            fixture.RunAsync(fixture.RefineOp(zenyCost: 100, expectedZeny: 100, AddNew((uint)(2000 + _), 1))))));

        Assert.Equal(1, responses.Count(r => r.Status == InventoryOperationStatus.Applied));
        Assert.Equal(7, responses.Count(r => r.Status == InventoryOperationStatus.PreconditionFailed));
        Assert.Equal(0u, await fixture.ZenyAsync()); // exactly one deduction landed, never negative, never double-charged.
        Assert.Single(await fixture.InventoryAsync()); // exactly one of the eight AddNew mutations persisted.
    }

    [SqlServerFact]
    public async Task ZenyCost_RepeatedOperationId_IsReplayed_NeverChargedTwice()
    {
        await using var fixture = await Fixture.CreateAsync(zeny: 500);
        var request = fixture.RefineOp(zenyCost: 75, expectedZeny: 500, AddNew(1010, 1));

        var first = await fixture.RunAsync(request);
        var lostAckRetry = await fixture.RunAsync(request); // same OperationId: a client resend after a lost ack.

        Assert.Equal(InventoryOperationStatus.Applied, first.Status);
        Assert.Equal(InventoryOperationStatus.Replayed, lostAckRetry.Status);
        Assert.Equal(425u, await fixture.ZenyAsync()); // deducted exactly once, not twice.
        Assert.Single(await fixture.InventoryAsync());
    }

    [SqlServerFact]
    public async Task ZenyCost_Zero_NeverTouchesTheCharacterRow_ExistingOperationsAreUnaffected()
    {
        await using var fixture = await Fixture.CreateAsync(zeny: 123);

        var response = await fixture.RunAsync(fixture.Op(AddNew(501, 1))); // ZenyCost defaults to 0.

        Assert.Equal(InventoryOperationStatus.Applied, response.Status);
        Assert.Equal(0u, response.RemainingZeny); // no currency involved in this operation at all.
        Assert.Equal(123u, await fixture.ZenyAsync()); // balance is exactly what it was - never read, never touched.
    }
}
