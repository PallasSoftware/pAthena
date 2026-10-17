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
    public void Engine_RejectsRowsTouchedTwiceAndEmptyAmounts()
    {
        var twice = InventoryOperationEngine.Evaluate(Op(
            new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 10, 1), new RemoveFromRowMutation(InventoryContainer.Inventory, 5, 1, 10, 1)), Rows());
        Assert.Equal((InventoryOperationStatus.Invalid, 1), (twice.Status, twice.FailedIndex));

        Assert.Equal(InventoryOperationStatus.Invalid, InventoryOperationEngine.Evaluate(Op(new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 0))), Rows()).Status);
        Assert.Equal(InventoryOperationStatus.Invalid, InventoryOperationEngine.Evaluate(new(0, 20, Id, InventoryOperationKind.Add, [new AddNewRowMutation(InventoryContainer.Inventory, InventoryItemRow.New(501, 1))]), Rows()).Status);
    }
}
