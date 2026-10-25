using System.Security.Cryptography;
using Athena.Net.MapServer.Net;
using Athena.Net.Shared.Inventory;

namespace MapServer.Tests.Testing;

/// <summary>
/// A CharServer stand-in for MapServer tests: the REAL shared operation rules (InventoryOperationEngine) over in-memory rows, with
/// CharServer's idempotent operation log. Because the rules are the same source the real executor compiles, a test here exercises exactly
/// the preconditions, replays and revision handling production has. Hooks simulate the failure modes: unanswered requests, lost answers,
/// failed reads.
/// </summary>
internal sealed class InMemoryInventoryOperations : ICharacterInventoryOperations
{
    private readonly object _gate = new();
    private readonly List<InventoryItemRow> _rows;
    // Storage/Cart (and GuildStorage) rows, modeled the same way the real CharServer executor
    // tracks them - a separate per-container row list, sharing the SAME durable-id sequence space
    // as inventory (matching production's single CharInventoryOperation-keyed identity domain per
    // character; a real deployment's Storage/Cart rows are a genuinely separate SQL table with
    // their own identity column, but nothing in this test double's own contract depends on the two
    // spaces colliding or not, so sharing one counter is the simplest faithful choice).
    private readonly Dictionary<InventoryContainer, List<InventoryItemRow>> _containerRows = [];
    private readonly Dictionary<Guid, (byte[] Hash, InventoryOperationResponse Response)> _log = [];
    private readonly Dictionary<Guid, PendingGroundDrop> _pendingDrops = [];
    private CharacterInventorySnapshot? _firstRead;
    private uint _nextId;
    private uint _zeny;

    public InMemoryInventoryOperations() : this(new CharacterInventorySnapshot([])) { }

    public InMemoryInventoryOperations(CharacterInventorySnapshot initial, uint zeny = 0)
    {
        _firstRead = initial;
        _rows = [.. initial.Items.Select(item => item.ToRow())];
        _nextId = _rows.Count == 0 ? 1 : _rows.Max(row => row.DurableId) + 1;
        _zeny = zeny;
    }

    /// <summary>The current in-memory zeny balance, mirroring CharCharacter.Zeny - mutated only by a request whose ZenyCost > 0, exactly like InventoryOperationExecutor's own real deduction.</summary>
    public uint Zeny { get { lock (_gate) return _zeny; } }

    /// <summary>Seeds a non-inventory container (Storage/Cart/GuildStorage) with initial rows - test setup only, never touched by production code.</summary>
    public void SeedContainer(InventoryContainer container, IReadOnlyList<InventoryItemRow> rows)
    {
        lock (_gate)
        {
            _containerRows[container] = [.. rows];
            var maxId = rows.Count == 0 ? 0 : rows.Max(r => r.DurableId);
            if (maxId >= _nextId) _nextId = maxId + 1;
        }
    }

    public IReadOnlyList<InventoryItemRow> ContainerRows(InventoryContainer container)
    {
        lock (_gate) return _containerRows.TryGetValue(container, out var rows) ? [.. rows.OrderBy(r => r.DurableId)] : [];
    }

    /// <summary>The next N requests are never answered and never applied (the request was lost on the way).</summary>
    public int DropRequests { get; set; }

    /// <summary>The next N requests are applied durably but their answer never reaches MapServer (lost acknowledgement).</summary>
    public int LoseAnswers { get; set; }

    /// <summary>Requests this predicate matches are never answered and never applied (selective outage, e.g. only operations that remove rows).</summary>
    public Func<InventoryOperationRequest, bool>? DropWhen { get; set; }

    public bool FailReads { get; set; }

    /// <summary>Every request received, including retries and replays.</summary>
    public List<InventoryOperationRequest> Requests { get; } = [];

    /// <summary>Requests that changed durable state (first application only).</summary>
    public List<InventoryOperationRequest> Applied { get; } = [];

    /// <summary>(row, new equip mask) of every Update mutation that was durably applied, in order.</summary>
    public List<(uint DurableId, uint Equip)> EquipUpdates =>
        [.. Applied.SelectMany(request => request.Mutations).OfType<UpdateRowMutation>().Select(update => (update.DurableId, update.NewRow.Equip))];

    public IReadOnlyList<InventoryItemRow> Rows
    {
        get { lock (_gate) return [.. _rows.OrderBy(row => row.DurableId)]; }
    }

    public int AmountOf(int itemId) => (int)Rows.Where(row => row.ItemId == itemId).Sum(row => (long)row.Amount);

    public Task<CharacterInventoryReadResult> GetInventoryAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (FailReads) return Task.FromResult(CharacterInventoryReadResult.Failed());
            // The initial snapshot (with its explicit, possibly sparse runtime slots) is returned until the durable state first changes; any
            // later read is what a real CharServer would answer: the current rows in stable order with dense slots.
            var first = Applied.Count == 0 ? _firstRead : null;
            return Task.FromResult(CharacterInventoryReadResult.Success(first ?? CharacterInventorySnapshot.FromRows([.. _rows.OrderBy(row => row.DurableId)])));
        }
    }

    public Task<InventoryOperationResponse?> ExecuteAsync(InventoryOperationRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add(request);
            if (DropRequests > 0 || DropWhen?.Invoke(request) == true)
            {
                if (DropRequests > 0) DropRequests--;
                return Task.FromResult<InventoryOperationResponse?>(null);
            }

            var hash = SHA256.HashData(InventoryOperationWire.BuildRequest(request));
            InventoryOperationResponse response;
            if (_log.TryGetValue(request.OperationId, out var logged))
            {
                response = logged.Hash.AsSpan().SequenceEqual(hash)
                    ? logged.Response with { Status = InventoryOperationStatus.Replayed }
                    : new(InventoryOperationStatus.Invalid, request.CharId, request.OperationId, -1, []);
            }
            else
            {
                response = Apply(request, hash);
            }

            if (LoseAnswers > 0 && response.Succeeded) { LoseAnswers--; return Task.FromResult<InventoryOperationResponse?>(null); }
            return Task.FromResult<InventoryOperationResponse?>(response);
        }
    }

    private List<InventoryItemRow> RowsFor(InventoryContainer container) =>
        container == InventoryContainer.Inventory ? _rows : _containerRows.TryGetValue(container, out var rows) ? rows : _containerRows[container] = [];

    public IReadOnlyList<PendingGroundDrop> PendingDrops { get { lock (_gate) return [.. _pendingDrops.Values]; } }

    public Task<IReadOnlyList<(PendingGroundDrop Record, InventoryItemRow Row)>?> GetPendingGroundDropsAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (FailReads) return Task.FromResult<IReadOnlyList<(PendingGroundDrop, InventoryItemRow)>?>(null);
            var rows = RowsFor(InventoryContainer.PendingGroundDrop);
            return Task.FromResult<IReadOnlyList<(PendingGroundDrop, InventoryItemRow)>?>(
                [.. _pendingDrops.Values.Select(record => (record, rows.Single(row => row.DurableId == record.ItemRowId)))]);
        }
    }

    private InventoryOperationResponse Apply(InventoryOperationRequest request, byte[] hash)
    {
        // CharServer's outbox rule (InventoryOperationExecutor): settling requires the record, and removes it.
        if (request.ResolvesPendingDrop != Guid.Empty && !_pendingDrops.ContainsKey(request.ResolvesPendingDrop))
            return new(InventoryOperationStatus.PreconditionFailed, request.CharId, request.OperationId, -1, []);
        var evaluation = InventoryOperationEngine.Evaluate(request, (container, id) => RowsFor(container).FirstOrDefault(row => row.DurableId == id), _zeny);
        if (evaluation.Status != InventoryOperationStatus.Applied)
            return new(evaluation.Status, request.CharId, request.OperationId, evaluation.FailedIndex, []);
        if (request.ZenyCost > 0) _zeny -= request.ZenyCost;

        var results = new List<InventoryMutationResult>();
        foreach (var change in evaluation.Changes)
        {
            var rows = RowsFor(change.Container);
            switch (change.Kind)
            {
                case InventoryRowChangeKind.Insert:
                {
                    var row = change.Row with { DurableId = _nextId++ };
                    rows.Add(row);
                    results.Add(new(row.DurableId, false, row));
                    break;
                }
                case InventoryRowChangeKind.Update:
                    rows[rows.FindIndex(r => r.DurableId == change.Row.DurableId)] = change.Row;
                    results.Add(new(change.Row.DurableId, false, change.Row));
                    break;
                case InventoryRowChangeKind.Delete:
                    rows.RemoveAll(r => r.DurableId == change.Row.DurableId);
                    results.Add(new(change.Row.DurableId, true, null));
                    break;
            }
        }

        if (request.ResolvesPendingDrop != Guid.Empty) _pendingDrops.Remove(request.ResolvesPendingDrop);
        if (request.RecordPendingDrop is { } pending)
        {
            var index = request.Mutations.ToList().FindIndex(m => m is AddNewRowMutation { Container: InventoryContainer.PendingGroundDrop });
            _pendingDrops[pending.DropId] = pending with { ItemRowId = results[index].DurableId };
        }
        var response = new InventoryOperationResponse(InventoryOperationStatus.Applied, request.CharId, request.OperationId, -1, results, request.ZenyCost > 0 ? _zeny : 0);
        _log[request.OperationId] = (hash, response);
        Applied.Add(request);
        return response;
    }
}
