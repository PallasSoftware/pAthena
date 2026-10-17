namespace Athena.Net.Shared.Inventory;

public enum InventoryRowChangeKind : byte { Insert, Update, Delete }

/// <summary>One row-level effect of a mutation. For Insert the row has DurableId 0 (assigned by storage) and Revision 1.</summary>
public sealed record InventoryRowChange(InventoryRowChangeKind Kind, InventoryContainer Container, InventoryItemRow Row);

public sealed record InventoryEvaluation(InventoryOperationStatus Status, int FailedIndex, IReadOnlyList<InventoryRowChange> Changes);

/// <summary>
/// The rules of an inventory operation as a pure function over already-loaded rows: structural validation and, per mutation, the
/// preconditions that make a stale writer fail instead of overwrite. Storage-agnostic on purpose: CharServer feeds it rows loaded from
/// SQL Server and persists the returned changes inside one transaction; tests feed it in-memory rows. One definition, no drift.
/// </summary>
public static class InventoryOperationEngine
{
    /// <summary>Structure that needs no stored state: no row touched twice, ids and amounts sane.</summary>
    public static bool IsWellFormed(InventoryOperationRequest request, out int badIndex)
    {
        badIndex = -1;
        if (request.CharId == 0 || request.AccountId == 0 || request.OperationId == Guid.Empty) return false;
        var seen = new HashSet<(InventoryContainer, uint)>();
        for (var i = 0; i < request.Mutations.Count; i++)
        {
            var ok = request.Mutations[i] switch
            {
                AddNewRowMutation add => add.Row.ItemId > 0 && add.Row.Amount > 0,
                AddToStackMutation stack => stack.DurableId > 0 && stack.Delta > 0 && stack.MaxAmount > 0 && seen.Add((stack.Container, stack.DurableId)),
                RemoveFromRowMutation remove => remove.DurableId > 0 && remove.Amount > 0 && seen.Add((remove.Container, remove.DurableId)),
                UpdateRowMutation update => update.DurableId > 0 && update.NewRow.ItemId > 0 && update.NewRow.Amount > 0 && seen.Add((update.Container, update.DurableId)),
                _ => false,
            };
            if (!ok) { badIndex = i; return false; }
        }
        return true;
    }

    /// <param name="findRow">The CURRENT stored row of a container, or null when it does not exist (or belongs to another owner).</param>
    public static InventoryEvaluation Evaluate(InventoryOperationRequest request, Func<InventoryContainer, uint, InventoryItemRow?> findRow)
    {
        if (!IsWellFormed(request, out var badIndex)) return new(InventoryOperationStatus.Invalid, badIndex, []);

        var changes = new List<InventoryRowChange>(request.Mutations.Count);
        for (var index = 0; index < request.Mutations.Count; index++)
        {
            var mutation = request.Mutations[index];
            InventoryRowChange? change = null;
            switch (mutation)
            {
                case AddNewRowMutation add:
                    change = new(InventoryRowChangeKind.Insert, add.Container, add.Row with { DurableId = 0, Revision = 1 });
                    break;
                case AddToStackMutation stack:
                {
                    var row = findRow(stack.Container, stack.DurableId);
                    if (row is not null && row.ItemId == stack.ExpectedItemId && row.Amount == stack.ExpectedAmount && row.Revision == stack.ExpectedRevision
                        && (ulong)row.Amount + stack.Delta <= stack.MaxAmount)
                        change = new(InventoryRowChangeKind.Update, stack.Container, row with { Amount = row.Amount + stack.Delta, Revision = row.Revision + 1 });
                    break;
                }
                case RemoveFromRowMutation remove:
                {
                    var row = findRow(remove.Container, remove.DurableId);
                    if (row is not null && row.Amount == remove.ExpectedAmount && row.Revision == remove.ExpectedRevision && remove.Amount <= row.Amount)
                    {
                        var left = row.Amount - remove.Amount;
                        change = left == 0
                            ? new(InventoryRowChangeKind.Delete, remove.Container, row)
                            : new(InventoryRowChangeKind.Update, remove.Container, row with { Amount = left, Revision = row.Revision + 1 });
                    }
                    break;
                }
                case UpdateRowMutation update:
                {
                    var row = findRow(update.Container, update.DurableId);
                    // Every column may change except the amount (add/remove mutations own it) and the identity.
                    if (row is not null && row.Revision == update.ExpectedRevision && row.Amount == update.NewRow.Amount)
                        change = new(InventoryRowChangeKind.Update, update.Container, update.NewRow with { DurableId = row.DurableId, Amount = row.Amount, Revision = row.Revision + 1 });
                    break;
                }
            }

            if (change is null) return new(InventoryOperationStatus.PreconditionFailed, index, []);
            changes.Add(change);
        }
        return new(InventoryOperationStatus.Applied, -1, changes);
    }
}
