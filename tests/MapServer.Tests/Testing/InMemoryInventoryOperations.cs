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
    private readonly Dictionary<Guid, (byte[] Hash, InventoryOperationResponse Response)> _log = [];
    private CharacterInventorySnapshot? _firstRead;
    private uint _nextId;

    public InMemoryInventoryOperations() : this(new CharacterInventorySnapshot([])) { }

    public InMemoryInventoryOperations(CharacterInventorySnapshot initial)
    {
        _firstRead = initial;
        _rows = [.. initial.Items.Select(item => item.ToRow())];
        _nextId = _rows.Count == 0 ? 1 : _rows.Max(row => row.DurableId) + 1;
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

    private InventoryOperationResponse Apply(InventoryOperationRequest request, byte[] hash)
    {
        var evaluation = InventoryOperationEngine.Evaluate(request, (container, id) =>
            container == InventoryContainer.Inventory ? _rows.FirstOrDefault(row => row.DurableId == id) : null);
        if (evaluation.Status != InventoryOperationStatus.Applied)
            return new(evaluation.Status, request.CharId, request.OperationId, evaluation.FailedIndex, []);

        var results = new List<InventoryMutationResult>();
        foreach (var change in evaluation.Changes)
        {
            // Only the inventory container is modeled here; other containers are accepted as no-ops for result shape.
            switch (change.Kind)
            {
                case InventoryRowChangeKind.Insert:
                {
                    var row = change.Row with { DurableId = _nextId++ };
                    _rows.Add(row);
                    results.Add(new(row.DurableId, false, row));
                    break;
                }
                case InventoryRowChangeKind.Update:
                    _rows[_rows.FindIndex(r => r.DurableId == change.Row.DurableId)] = change.Row;
                    results.Add(new(change.Row.DurableId, false, change.Row));
                    break;
                case InventoryRowChangeKind.Delete:
                    _rows.RemoveAll(r => r.DurableId == change.Row.DurableId);
                    results.Add(new(change.Row.DurableId, true, null));
                    break;
            }
        }

        var response = new InventoryOperationResponse(InventoryOperationStatus.Applied, request.CharId, request.OperationId, -1, results);
        _log[request.OperationId] = (hash, response);
        Applied.Add(request);
        return response;
    }
}
