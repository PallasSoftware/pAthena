using Athena.Net.Shared.Inventory;

namespace Athena.Net.CharServer.Tests.Inventory;

// Pure tests of the shared inventory contract (no database): the wire codec and the operation rules both servers compile.
public sealed class InventoryWireAndEngineTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static InventoryItemRow FullRow() => new(
        DurableId: 77, ItemId: 1201, Amount: 3, Equip: 2, Identify: 1, Refine: 9, Attribute: 1, Favorite: 1, Bound: 4, EnchantGrade: 2,
        Card0: 4001, Card1: 4002, Card2: 0, Card3: 4004,
        Option0: new(1, -2, 3), Option1: new(4, 5, 6), Option2: default, Option3: new(-7, 8, 9), Option4: new(10, 11, 12),
        ExpireTime: 1_900_000_000, UniqueId: ulong.MaxValue - 5, EquipSwitch: 0x20, Revision: 14);

    [Fact]
    public void Row_RoundTripsEveryField()
    {
        var buffer = new byte[InventoryItemRow.WireLength];
        FullRow().Write(buffer);
        Assert.Equal(FullRow(), InventoryItemRow.Read(buffer));
    }

    [Fact]
    public void Request_RoundTripsEveryMutationKind()
    {
        var request = new InventoryOperationRequest(10, 20, Id, InventoryOperationKind.Refine,
        [
            new AddNewRowMutation(InventoryContainer.Storage, FullRow() with { DurableId = 0, Revision = 0 }),
            new AddToStackMutation(InventoryContainer.Inventory, 5, 501, 7, 20, 30_000, 3),
            new RemoveFromRowMutation(InventoryContainer.Cart, 6, 2, 9, 4),
            new UpdateRowMutation(InventoryContainer.GuildStorage, 7, 8, FullRow()),
        ]);

        var bytes = InventoryOperationWire.BuildRequest(request);

        Assert.True(InventoryOperationWire.TryParseRequest(bytes, out var parsed));
        Assert.Equal((request.AccountId, request.CharId, request.OperationId, request.Kind), (parsed.AccountId, parsed.CharId, parsed.OperationId, parsed.Kind));
        Assert.Equal(request.Mutations, parsed.Mutations);
    }

    [Fact]
    public void Request_RoundTripsZenyCostAndExpectedZeny()
    {
        var request = new InventoryOperationRequest(10, 20, Id, InventoryOperationKind.Refine,
            [new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 1, 1)], ZenyCost: 500, ExpectedZeny: 12_345);

        var bytes = InventoryOperationWire.BuildRequest(request);

        Assert.True(InventoryOperationWire.TryParseRequest(bytes, out var parsed));
        Assert.Equal((500u, 12_345u), (parsed.ZenyCost, parsed.ExpectedZeny));
    }

    [Fact]
    public void Response_RoundTripsRemainingZeny()
    {
        var response = new InventoryOperationResponse(InventoryOperationStatus.Applied, 20, Id, -1, [], RemainingZeny: 9_876);

        Assert.True(InventoryOperationWire.TryParseResponse(InventoryOperationWire.BuildResponse(response), out var parsed));
        Assert.Equal(9_876u, parsed.RemainingZeny);
    }

    [Fact]
    public void Request_RejectsTruncatedOversizedAndUnknownContent()
    {
        var bytes = InventoryOperationWire.BuildRequest(new(10, 20, Id, InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 1))]));

        Assert.False(InventoryOperationWire.TryParseRequest(bytes[..^1], out _));
        Assert.False(InventoryOperationWire.TryParseRequest([.. bytes, 0], out _));
        var unknownTag = (byte[])bytes.Clone(); unknownTag[InventoryOperationWire.RequestHeaderLength] = 99;
        Assert.False(InventoryOperationWire.TryParseRequest(unknownTag, out _));
        var unknownContainer = (byte[])bytes.Clone(); unknownContainer[InventoryOperationWire.RequestHeaderLength + 1] = 99;
        Assert.False(InventoryOperationWire.TryParseRequest(unknownContainer, out _));
        Assert.Throws<ArgumentException>(() => InventoryOperationWire.BuildRequest(new(10, 20, Id, InventoryOperationKind.Add, [])));
    }

    [Fact]
    public void Response_RoundTripsResultsIncludingDeletedRows()
    {
        var response = new InventoryOperationResponse(InventoryOperationStatus.PreconditionFailed, 20, Id, 2,
            [new InventoryMutationResult(77, false, FullRow()), new InventoryMutationResult(78, true, null)]);

        Assert.True(InventoryOperationWire.TryParseResponse(InventoryOperationWire.BuildResponse(response), out var parsed));

        Assert.Equal((response.Status, response.CharId, response.OperationId, response.FailedIndex), (parsed.Status, parsed.CharId, parsed.OperationId, parsed.FailedIndex));
        Assert.Equal(response.Results, parsed.Results);
    }

    [Fact]
    public void ContainerList_RoundTrips_AndRejectsDuplicatedDurableIds()
    {
        var rows = new[] { FullRow(), FullRow() with { DurableId = 78 } };
        var response = InventoryListWire.BuildResponse(0, 20, InventoryContainer.Cart, rows);
        Assert.True(InventoryListWire.TryParseResponse(response, out var result, out var charId, out var container, out var parsed));
        Assert.Equal((0, 20u, InventoryContainer.Cart), (result, charId, container));
        Assert.Equal(rows, parsed);

        var duplicated = InventoryListWire.BuildResponse(0, 20, InventoryContainer.Inventory, [FullRow(), FullRow()]);
        Assert.False(InventoryListWire.TryParseResponse(duplicated, out _, out _, out _, out _));

        Assert.True(InventoryListWire.TryParseGet(InventoryListWire.BuildGetRequest(10, 20, InventoryContainer.Storage), out var account, out var character, out var which));
        Assert.Equal((10u, 20u, InventoryContainer.Storage), (account, character, which));
    }

    // ---- engine ----

    private static InventoryOperationRequest Op(params InventoryMutation[] mutations) => new(10, 20, Id, InventoryOperationKind.Add, mutations);

    private static Func<InventoryContainer, uint, InventoryItemRow?> Rows(params InventoryItemRow[] rows) =>
        (_, id) => rows.FirstOrDefault(row => row.DurableId == id);

    [Fact]
    public void Engine_AddNew_InsertsWithRevisionOne()
    {
        var evaluation = InventoryOperationEngine.Evaluate(Op(new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 4))), Rows());

        Assert.Equal(InventoryOperationStatus.Applied, evaluation.Status);
        var change = Assert.Single(evaluation.Changes);
        Assert.Equal((InventoryRowChangeKind.Insert, 501u, 4u, 1u), (change.Kind, change.Row.ItemId, change.Row.Amount, change.Row.Revision));
    }

    [Fact]
    public void Engine_AppliesPreconditionsToStackRemoveAndUpdate()
    {
        var row = InventoryItemRow.New(501, 10) with { DurableId = 5, Revision = 3 };

        var overflow = InventoryOperationEngine.Evaluate(Op(new AddToStackMutation(InventoryContainer.Inventory, 5, 501, 21, 10, 30, 3)), Rows(row));
        Assert.Equal((InventoryOperationStatus.PreconditionFailed, 0), (overflow.Status, overflow.FailedIndex));

        var staleRevision = InventoryOperationEngine.Evaluate(Op(new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 10, 2)), Rows(row));
        Assert.Equal(InventoryOperationStatus.PreconditionFailed, staleRevision.Status);

        var tooMuch = InventoryOperationEngine.Evaluate(Op(new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 11, 10, 3)), Rows(row));
        Assert.Equal(InventoryOperationStatus.PreconditionFailed, tooMuch.Status);

        var amountChange = InventoryOperationEngine.Evaluate(Op(new UpdateRowMutation(InventoryContainer.Inventory, 5, 3, row with { Amount = 99 })), Rows(row));
        Assert.Equal(InventoryOperationStatus.PreconditionFailed, amountChange.Status);

        var removeAll = InventoryOperationEngine.Evaluate(Op(new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 10, 10, 3)), Rows(row));
        Assert.Equal(InventoryRowChangeKind.Delete, Assert.Single(removeAll.Changes).Kind);

        var update = InventoryOperationEngine.Evaluate(Op(new UpdateRowMutation(InventoryContainer.Inventory, 5, 3, row with { Refine = 4 })), Rows(row));
        var updated = Assert.Single(update.Changes).Row;
        Assert.Equal((4, 4u), ((int)updated.Refine, updated.Revision));
    }

    [Fact]
    public void Engine_FailsTheWholeOperationAtTheFirstBadMutation()
    {
        var row = InventoryItemRow.New(501, 10) with { DurableId = 5, Revision = 1 };

        var evaluation = InventoryOperationEngine.Evaluate(Op(
            new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(502, 1)),
            new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 99, 1)), Rows(row));

        Assert.Equal((InventoryOperationStatus.PreconditionFailed, 1), (evaluation.Status, evaluation.FailedIndex));
        Assert.Empty(evaluation.Changes);
    }

    [Fact]
    public void Engine_ZenyCost_RejectsInsufficientFunds_WithNoRowChangesComputed()
    {
        var row = InventoryItemRow.New(501, 10) with { DurableId = 5, Revision = 1 };
        var request = new InventoryOperationRequest(10, 20, Id, InventoryOperationKind.Refine,
            [new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 10, 1)], ZenyCost: 100, ExpectedZeny: 99);

        var evaluation = InventoryOperationEngine.Evaluate(request, Rows(row), currentZeny: 99);

        Assert.Equal((InventoryOperationStatus.PreconditionFailed, -1), (evaluation.Status, evaluation.FailedIndex));
        Assert.Empty(evaluation.Changes); // not even the well-formed row mutation was evaluated.
    }

    [Fact]
    public void Engine_ZenyCost_RejectsStaleExpectedZeny_EvenWhenSufficientForTheCost()
    {
        var row = InventoryItemRow.New(501, 10) with { DurableId = 5, Revision = 1 };
        var request = new InventoryOperationRequest(10, 20, Id, InventoryOperationKind.Refine,
            [new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 10, 1)], ZenyCost: 100, ExpectedZeny: 1000);

        // Current balance (1500) is plenty for ZenyCost (100), but does not match the caller's stale ExpectedZeny (1000).
        var evaluation = InventoryOperationEngine.Evaluate(request, Rows(row), currentZeny: 1500);

        Assert.Equal(InventoryOperationStatus.PreconditionFailed, evaluation.Status);
    }

    [Fact]
    public void Engine_ZenyCost_Zero_NeverConsultsCurrentZeny()
    {
        var row = InventoryItemRow.New(501, 10) with { DurableId = 5, Revision = 1 };
        var request = Op(new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 10, 10, 1)); // ZenyCost defaults to 0.

        // currentZeny: 0 (the default) even though a real caller's balance could be anything - ZenyCost=0 means
        // this operation involves no currency at all, so the check must never gate on it.
        var evaluation = InventoryOperationEngine.Evaluate(request, Rows(row));

        Assert.Equal(InventoryOperationStatus.Applied, evaluation.Status);
    }

    [Fact]
    public void Engine_RejectsRowsTouchedTwiceAndEmptyAmounts()
    {
        var twice = InventoryOperationEngine.Evaluate(Op(
            new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 10, 1), new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 10, 1)), Rows());
        Assert.Equal((InventoryOperationStatus.Invalid, 1), (twice.Status, twice.FailedIndex));

        Assert.Equal(InventoryOperationStatus.Invalid, InventoryOperationEngine.Evaluate(Op(new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 0))), Rows()).Status);
        Assert.Equal(InventoryOperationStatus.Invalid, InventoryOperationEngine.Evaluate(new(0, 20, Id, InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 1))]), Rows()).Status);
    }

    // ---- player-drop outbox (PendingGroundDrop) ----

    private static readonly PendingGroundDrop Pending = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "prontera", 150, 180, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    private static InventoryOperationRequest Record(params InventoryMutation[] mutations) => new(10, 20, Id, InventoryOperationKind.Drop, mutations, RecordPendingDrop: Pending);

    [Fact]
    public void Request_RoundTripsThePendingDropTrailers()
    {
        var record = Record(new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 2, 5, 1), new AddNewRowMutation(InventoryContainer.PendingGroundDrop, FullRow() with { DurableId = 0 }));
        var settle = new InventoryOperationRequest(10, 20, Id, InventoryOperationKind.Drop, [new RemoveFromRowMutation(InventoryContainer.PendingGroundDrop, 9, 2, 2, 1)], ResolvesPendingDrop: Pending.DropId);

        Assert.True(InventoryOperationWire.TryParseRequest(InventoryOperationWire.BuildRequest(record), out var parsedRecord));
        Assert.True(InventoryOperationWire.TryParseRequest(InventoryOperationWire.BuildRequest(settle), out var parsedSettle));
        Assert.Equal(Pending, parsedRecord.RecordPendingDrop);
        Assert.Equal(Pending.DropId, parsedSettle.ResolvesPendingDrop);
        Assert.Null(parsedSettle.RecordPendingDrop);
    }

    [Fact]
    public void PendingContainer_ChangesOnlyThroughItsOutboxRecord()
    {
        var add = new AddNewRowMutation(InventoryContainer.PendingGroundDrop, FullRow() with { DurableId = 0 });
        var remove = new RemoveFromRowMutation(InventoryContainer.PendingGroundDrop, 9, 1, 1, 1);

        Assert.True(InventoryOperationEngine.IsWellFormed(Record(add), out _));
        Assert.False(InventoryOperationEngine.IsWellFormed(Record(), out _));        // a record with no pending row
        Assert.False(InventoryOperationEngine.IsWellFormed(Record(add, add), out _)); // two pending rows for one record
        Assert.False(InventoryOperationEngine.IsWellFormed(new(10, 20, Id, InventoryOperationKind.Drop, [add]), out _));    // orphan pending row
        Assert.False(InventoryOperationEngine.IsWellFormed(new(10, 20, Id, InventoryOperationKind.Drop, [remove]), out _)); // unsettled removal
        Assert.True(InventoryOperationEngine.IsWellFormed(new(10, 20, Id, InventoryOperationKind.Drop, [remove], ResolvesPendingDrop: Pending.DropId), out _));
    }

    [Fact]
    public void PendingDropList_RoundTrips()
    {
        var records = new[] { Pending with { ItemRowId = 9 }, Pending with { DropId = Guid.NewGuid(), MapId = "izlude", ItemRowId = 10 } };

        Assert.True(PendingGroundDropListWire.TryParseResponse(PendingGroundDropListWire.BuildResponse(0, 20, records), out var result, out var charId, out var parsed));
        Assert.Equal((0, 20u), (result, charId));
        Assert.Equal(records, parsed);
    }
}
