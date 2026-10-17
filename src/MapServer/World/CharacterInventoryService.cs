using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.Shared.Inventory;

namespace Athena.Net.MapServer.World;

public enum InventoryChangeStatus
{
    /// <summary>Durably applied (or replayed from CharServer's operation log) and reflected in the snapshot.</summary>
    Applied,
    /// <summary>The planner declined (nothing to do / not allowed); nothing was sent.</summary>
    Rejected,
    /// <summary>CharServer refused because the rows changed underneath the plan; nothing was applied and the snapshot was reloaded.</summary>
    Conflict,
    /// <summary>CharServer could not be reached or reloaded; nothing is known to have been applied.</summary>
    Unavailable,
    /// <summary>No answer after every attempt: the operation MAY have been applied. Re-send it with ResolveAsync before giving up the item.</summary>
    InDoubt,
}

public sealed record InventoryChange(InventoryChangeStatus Status, CharacterInventorySnapshot Snapshot, IReadOnlyList<InventoryMutationResult> Results, Guid OperationId)
{
    public bool Succeeded => Status == InventoryChangeStatus.Applied;

    /// <summary>For a rejected grant: the pinned pc_additem result (overweight, no free slot, ...) that stopped it.</summary>
    public AddItemResult? Failure { get; init; }

    /// <summary>The mutations that were sent (same order as <see cref="Results"/>).</summary>
    public IReadOnlyList<InventoryMutation> Mutations { get; init; } = [];

    /// <summary>
    /// For a grant: the rows that received items and how many each received, in mutation order. A stack extension reports its delta; a new
    /// row reports its whole amount. Rows of other containers are skipped.
    /// </summary>
    public IReadOnlyList<(CharacterInventoryItem Row, uint Added)> Placements() =>
        [.. Results.Select((result, index) => (result, mutation: Mutations[index]))
            .Where(p => p.mutation.Container == InventoryContainer.Inventory && !p.result.Deleted
                        && p.mutation is AddNewRowMutation or AddToStackMutation)
            .Select(p => (Snapshot.Items.Single(item => item.DurableId == p.result.DurableId),
                          p.mutation is AddToStackMutation stack ? stack.Delta : ((AddNewRowMutation)p.mutation).Row.Amount))];
}

// The per-session owner of the character's inventory snapshot. Every change is
//   plan (from the CURRENT snapshot, under the gate)  ->  one durable operation  ->  apply the confirmed results to the snapshot.
// CharServer is the authority: the snapshot only ever changes from a confirmed operation result or a full reload, never speculatively,
// and a client success is only projected after Applied. An unanswered operation is re-sent with the SAME id (CharServer applies it at
// most once); if it stays unanswered the change is InDoubt and the service reloads before planning anything else.
public sealed class CharacterInventoryService
{
    private readonly uint _accountId;
    private readonly uint _charId;
    private readonly ICharacterInventoryOperations _operations;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, InventoryOperationRequest> _inDoubt = [];
    private CharacterInventorySnapshot _snapshot;
    private bool _needsReload;

    private readonly IInventoryRules _rules;
    private readonly IReadOnlyDictionary<int, ItemDefinition> _items;
    private readonly Func<InventoryLimits> _limits;

    public CharacterInventoryService(
        uint accountId, uint charId, ICharacterInventoryOperations operations, CharacterInventorySnapshot initial,
        IInventoryRules rules, IReadOnlyDictionary<int, ItemDefinition> items, Func<InventoryLimits>? limits = null,
        int maxAttempts = 3, TimeSpan? retryDelay = null)
    {
        _rules = rules;
        _items = items;
        // Without a stats-derived provider the character gets the base inventory and no practical weight limit (tests, tools).
        _limits = limits ?? (() => new InventoryLimits(100, uint.MaxValue));
        _accountId = accountId;
        _charId = charId;
        _operations = operations;
        _snapshot = initial;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
    }

    public CharacterInventorySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public IReadOnlyCollection<Guid> InDoubtOperations
    {
        get { lock (_inDoubt) return [.. _inDoubt.Keys]; }
    }

    /// <summary>Plans and applies one durable change. <paramref name="plan"/> sees the current snapshot and returns the mutations, or null to decline.</summary>
    public async Task<InventoryChange> ExecuteAsync(
        InventoryOperationKind kind,
        Func<CharacterInventorySnapshot, IReadOnlyList<InventoryMutation>?> plan,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_needsReload && !await ReloadAsync(cancellationToken))
                return new(InventoryChangeStatus.Unavailable, Snapshot, [], Guid.Empty);

            var mutations = plan(Snapshot);
            if (mutations is null || mutations.Count == 0) return new(InventoryChangeStatus.Rejected, Snapshot, [], Guid.Empty);

            var request = new InventoryOperationRequest(_accountId, _charId, Guid.NewGuid(), kind, mutations);
            return await SendAsync(request, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Grants several items in ONE durable operation (all or nothing). The regional pc_additem rules decide each unit against the capacity and
    /// weight limits current at planning time; when one does not fit, nothing is sent and <see cref="InventoryChange.Failure"/> carries its code.
    /// </summary>
    public async Task<InventoryChange> GrantAsync(IReadOnlyList<InventoryPlanner.GrantRequest> grants, InventoryOperationKind kind, CancellationToken cancellationToken)
    {
        InventoryPlanner.GrantPlan? plan = null;
        var change = await ExecuteAsync(kind, inventory =>
        {
            plan = InventoryPlanner.PlanGrants(_rules, inventory, _limits(), _items, grants);
            return plan.Mutations;
        }, cancellationToken);
        return plan is { Succeeded: false } ? change with { Failure = plan.Result } : change;
    }

    /// <summary>
    /// Consumes units of one row and creates the granted items in ONE durable operation (a container opened: the box is gone exactly when its
    /// contents exist). The grants are planned against the inventory as it is AFTER the consumption, so the freed slot and weight count.
    /// </summary>
    public async Task<InventoryChange> ConsumeAndGrantAsync(uint durableId, uint consume, IReadOnlyList<InventoryPlanner.GrantRequest> grants, InventoryOperationKind kind, CancellationToken cancellationToken)
    {
        InventoryPlanner.GrantPlan? plan = null;
        var change = await ExecuteAsync(kind, inventory =>
        {
            var row = inventory.Items.FirstOrDefault(item => item.DurableId == durableId);
            if (row is null || row.Amount < consume) return null;

            var remaining = row.Amount - consume;
            var afterConsumption = remaining == 0
                ? inventory.WithoutDurableId(durableId)
                : inventory.WithUpdatedItem(durableId, row.ItemId, remaining, row.Equip, row.Identified, row.Refine, row.Favorite, row.Bound);
            plan = InventoryPlanner.PlanGrants(_rules, afterConsumption, _limits(), _items, grants);
            return plan.Mutations is null ? null : [InventoryPlanner.RemoveFromRow(row, consume), .. plan.Mutations];
        }, cancellationToken);
        return plan is { Succeeded: false } ? change with { Failure = plan.Result } : change;
    }

    public Task<InventoryChange> GrantAsync(IReadOnlyList<(ItemDefinition Definition, uint Amount)> grants, InventoryOperationKind kind, CancellationToken cancellationToken) =>
        GrantAsync([.. grants.Select(grant => new InventoryPlanner.GrantRequest(grant.Definition, grant.Amount))], kind, cancellationToken);

    public Task<InventoryChange> GrantAsync(ItemDefinition definition, uint amount, CancellationToken cancellationToken) =>
        GrantAsync([new InventoryPlanner.GrantRequest(definition, amount)], InventoryOperationKind.Add, cancellationToken);

    /// <summary>Removes an amount of an item id across its rows in ONE durable operation.</summary>
    public Task<InventoryChange> RemoveByItemIdAsync(int itemId, uint amount, CancellationToken cancellationToken) =>
        ExecuteAsync(InventoryOperationKind.Consume, inventory => InventoryPlanner.PlanRemoveByItemId(inventory, itemId, amount), cancellationToken);

    /// <summary>Re-sends an operation that was InDoubt. Safe at any time: CharServer applies it once or replays the recorded outcome.</summary>
    public async Task<InventoryChange> ResolveAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            InventoryOperationRequest? request;
            lock (_inDoubt) _inDoubt.TryGetValue(operationId, out request);
            if (request is null) return new(InventoryChangeStatus.Rejected, Snapshot, [], operationId);
            return await SendAsync(request, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Discards the cache and reads the authoritative rows again (after a conflict, an in-doubt operation or a reconnect).</summary>
    public async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        var read = await _operations.GetInventoryAsync(_accountId, _charId, cancellationToken);
        if (!read.Succeeded) { _needsReload = true; return false; }
        Volatile.Write(ref _snapshot, read.Snapshot!);
        _needsReload = false;
        return true;
    }

    private async Task<InventoryChange> SendAsync(InventoryOperationRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            InventoryOperationResponse? response = null;
            try { response = await _operations.ExecuteAsync(request, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MapLogger.Warning($"Inventory operation {request.OperationId} send failed attempt={attempt} error={ex.GetType().Name}.");
            }

            switch (response?.Status)
            {
                case InventoryOperationStatus.Applied or InventoryOperationStatus.Replayed:
                    lock (_inDoubt) _inDoubt.Remove(request.OperationId);
                    return await ApplyAsync(request, response, cancellationToken);
                case InventoryOperationStatus.PreconditionFailed:
                    lock (_inDoubt) _inDoubt.Remove(request.OperationId);
                    MapLogger.Warning($"Inventory operation {request.OperationId} charId={_charId} rejected: a row changed (failedIndex={response.FailedIndex}); reloading.");
                    await ReloadAsync(cancellationToken);
                    return new(InventoryChangeStatus.Conflict, Snapshot, [], request.OperationId);
                case InventoryOperationStatus.Invalid:
                    lock (_inDoubt) _inDoubt.Remove(request.OperationId);
                    MapLogger.Warning($"Inventory operation {request.OperationId} charId={_charId} rejected as invalid (failedIndex={response.FailedIndex}).");
                    return new(InventoryChangeStatus.Rejected, Snapshot, [], request.OperationId);
            }

            if (attempt < _maxAttempts && _retryDelay > TimeSpan.Zero) await Task.Delay(_retryDelay, cancellationToken);
        }

        // Nothing answered: the operation may have been applied. Keep the request so the caller can resolve it, and distrust the cache.
        lock (_inDoubt) _inDoubt[request.OperationId] = request;
        _needsReload = true;
        MapLogger.Warning($"Inventory operation {request.OperationId} charId={_charId} is in doubt after {_maxAttempts} attempts.");
        return new(InventoryChangeStatus.InDoubt, Snapshot, [], request.OperationId);
    }

    private async Task<InventoryChange> ApplyAsync(InventoryOperationRequest request, InventoryOperationResponse response, CancellationToken cancellationToken)
    {
        // Only the inventory container lives in this snapshot; cart/storage results are returned to the caller untouched.
        var inventoryResults = response.Results.Where((_, index) => request.Mutations[index].Container == InventoryContainer.Inventory).ToList();
        try
        {
            Volatile.Write(ref _snapshot, Snapshot.Apply(inventoryResults));
        }
        catch (InvalidOperationException ex)
        {
            // The durable result contradicts the cache (e.g. a row the cache never had): the durable side is right, so reload.
            MapLogger.Warning($"Inventory operation {request.OperationId} result contradicts the cache ({ex.Message}); reloading.");
            if (!await ReloadAsync(cancellationToken)) _needsReload = true;
        }
        return new(InventoryChangeStatus.Applied, Snapshot, response.Results, request.OperationId) { Mutations = request.Mutations };
    }
}
