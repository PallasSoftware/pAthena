using System.Security.Cryptography;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Logging;
using Athena.Net.Shared.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.CharServer.Inventory;

// Applies one inventory operation (a batch of row mutations on a character's containers) atomically and idempotently.
//
//   * Atomic: every mutation and the operation-log row are written in ONE database transaction; any failed precondition rolls
//     the whole operation back (nothing is applied, FailedIndex names the mutation).
//   * Idempotent: the operation-log row is keyed by the caller's OperationId. A repeated id (lost acknowledgement, reconnect,
//     MapServer retry) returns the recorded outcome with status Replayed and applies nothing; the same id with a different
//     payload is rejected. A concurrent duplicate loses on the log's primary key and is converted into a replay.
//   * No stale overwrites: every mutation of an existing row carries the row revision/amount the caller saw.
//   * CharServer never decides stacking: callers name the stack to extend (or ask for a new row).
internal sealed class InventoryOperationExecutor(Func<CharDbContext?> dbFactory, TimeProvider? timeProvider = null)
{
    // Operation-log rows older than this are dropped opportunistically. A retry of an operation this old can no longer be answered
    // from the log, which is why MapServer finishes or abandons an operation within seconds.
    internal static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal async Task<InventoryOperationResponse> ExecuteAsync(InventoryOperationRequest request, byte[] requestBytes, CancellationToken cancellationToken)
    {
        if (!IsWellFormed(request, out var badIndex)) return Respond(InventoryOperationStatus.Invalid, request, badIndex);

        CharDbContext? db;
        try { db = dbFactory(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Respond(InventoryOperationStatus.Unavailable, request, -1); }
        if (db is null) return Respond(InventoryOperationStatus.Unavailable, request, -1);

        await using (db)
        {
            var hash = SHA256.HashData(requestBytes);
            try
            {
                var strategy = db.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    return await ApplyAsync(db, request, hash, cancellationToken);
                });
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // A concurrent execution of the same OperationId committed first: answer from the log, never apply twice.
                return await ReplayOrFailAsync(dbFactory, request, hash, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Respond(InventoryOperationStatus.PreconditionFailed, request, -1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharLogger.Warning($"Inventory operation {request.OperationId} charId={request.CharId} failed reason=database-error error={ex.GetType().Name}.");
                return Respond(InventoryOperationStatus.Unavailable, request, -1);
            }
        }
    }

    private async Task<InventoryOperationResponse> ApplyAsync(CharDbContext db, InventoryOperationRequest request, byte[] hash, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.InventoryOperations.AsNoTracking().FirstOrDefaultAsync(o => o.OperationId == request.OperationId, cancellationToken);
        if (existing is not null) return Replay(existing, request, hash);

        uint? guildId = null;
        if (request.Mutations.Any(m => m.Container == InventoryContainer.GuildStorage))
        {
            guildId = await db.Characters.AsNoTracking().Where(c => c.CharId == request.CharId && c.AccountId == request.AccountId).Select(c => (uint?)c.GuildId).FirstOrDefaultAsync(cancellationToken);
            if (guildId is null or 0) return Respond(InventoryOperationStatus.Invalid, request, request.Mutations.Select((m, i) => (m, i)).First(p => p.m.Container == InventoryContainer.GuildStorage).i);
        }

        var touched = new List<(IItemRowColumns Entity, bool Deleted, uint DurableId)>();
        for (var index = 0; index < request.Mutations.Count; index++)
        {
            var mutation = request.Mutations[index];
            var owner = OwnerOf(mutation.Container, request, guildId);
            switch (mutation)
            {
                case AddNewRowMutation add:
                {
                    IItemRowColumns entity = add.Container == InventoryContainer.Inventory
                        ? new CharInventory { CharId = request.CharId }
                        : new CharContainerItem { Container = (byte)add.Container, OwnerId = owner };
                    ItemRowMapper.ApplyNew(entity, add.Row);
                    entity.Revision = 1;
                    db.Add(entity);
                    touched.Add((entity, false, 0));
                    break;
                }
                case AddToStackMutation stack:
                {
                    var entity = await FindAsync(db, stack.Container, owner, stack.DurableId, cancellationToken);
                    if (entity is null || entity.NameId != stack.ExpectedItemId || entity.Amount != stack.ExpectedAmount || entity.Revision != stack.ExpectedRevision
                        || stack.Delta == 0 || (ulong)entity.Amount + stack.Delta > stack.MaxAmount)
                        return Respond(InventoryOperationStatus.PreconditionFailed, request, index);
                    entity.Amount += stack.Delta;
                    entity.Revision++;
                    touched.Add((entity, false, entity.Id));
                    break;
                }
                case RemoveFromRowMutation remove:
                {
                    var entity = await FindAsync(db, remove.Container, owner, remove.DurableId, cancellationToken);
                    if (entity is null || entity.Amount != remove.ExpectedAmount || entity.Revision != remove.ExpectedRevision
                        || remove.Amount == 0 || remove.Amount > entity.Amount)
                        return Respond(InventoryOperationStatus.PreconditionFailed, request, index);
                    entity.Amount -= remove.Amount;
                    if (entity.Amount == 0) { db.Remove(entity); touched.Add((entity, true, entity.Id)); }
                    else { entity.Revision++; touched.Add((entity, false, entity.Id)); }
                    break;
                }
                case UpdateRowMutation update:
                {
                    var entity = await FindAsync(db, update.Container, owner, update.DurableId, cancellationToken);
                    if (entity is null || entity.Revision != update.ExpectedRevision || entity.Amount != update.NewRow.Amount)
                        return Respond(InventoryOperationStatus.PreconditionFailed, request, index);
                    ItemRowMapper.ApplyMutable(entity, update.NewRow);
                    entity.Revision++;
                    touched.Add((entity, false, entity.Id));
                    break;
                }
            }
        }

        // First save: the item mutations (assigns generated ids; row-revision conflicts surface here).
        await db.SaveChangesAsync(cancellationToken);

        var results = touched.Select(t => new InventoryMutationResult(t.Deleted ? t.DurableId : t.Entity.Id, t.Deleted, t.Deleted ? null : ItemRowMapper.ToRow(t.Entity))).ToList();
        var response = new InventoryOperationResponse(InventoryOperationStatus.Applied, request.CharId, request.OperationId, -1, results);

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        db.InventoryOperations.Add(new CharInventoryOperation
        {
            OperationId = request.OperationId, CharId = request.CharId, Kind = (byte)request.Kind, PayloadHash = hash,
            ResponseBytes = InventoryOperationWire.BuildResponse(response), CreatedAt = now,
        });
        // Second save in the same transaction: the log row. A concurrent duplicate OperationId fails here on the primary key.
        await db.SaveChangesAsync(cancellationToken);

        if ((request.OperationId.GetHashCode() & 0x1F) == 0)
        {
            var cutoff = now - (long)LogRetention.TotalSeconds;
            await db.InventoryOperations.Where(o => o.CharId == request.CharId && o.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private static InventoryOperationResponse Replay(CharInventoryOperation existing, InventoryOperationRequest request, byte[] hash)
    {
        if (existing.CharId != request.CharId || !existing.PayloadHash.AsSpan().SequenceEqual(hash))
            return Respond(InventoryOperationStatus.Invalid, request, -1);
        return InventoryOperationWire.TryParseResponse(existing.ResponseBytes, out var stored)
            ? stored with { Status = InventoryOperationStatus.Replayed }
            : Respond(InventoryOperationStatus.Unavailable, request, -1);
    }

    private async Task<InventoryOperationResponse> ReplayOrFailAsync(Func<CharDbContext?> factory, InventoryOperationRequest request, byte[] hash, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = factory();
            if (db is null) return Respond(InventoryOperationStatus.Unavailable, request, -1);
            var existing = await db.InventoryOperations.AsNoTracking().FirstOrDefaultAsync(o => o.OperationId == request.OperationId, cancellationToken);
            return existing is null ? Respond(InventoryOperationStatus.Unavailable, request, -1) : Replay(existing, request, hash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Respond(InventoryOperationStatus.Unavailable, request, -1);
        }
    }

    private static async Task<IItemRowColumns?> FindAsync(CharDbContext db, InventoryContainer container, uint owner, uint durableId, CancellationToken cancellationToken) =>
        container == InventoryContainer.Inventory
            ? await db.Inventory.FirstOrDefaultAsync(i => i.Id == durableId && i.CharId == owner, cancellationToken)
            : await db.ContainerItems.FirstOrDefaultAsync(i => i.Id == durableId && i.Container == (byte)container && i.OwnerId == owner, cancellationToken);

    private static uint OwnerOf(InventoryContainer container, InventoryOperationRequest request, uint? guildId) => container switch
    {
        InventoryContainer.Storage => request.AccountId,
        InventoryContainer.GuildStorage => guildId ?? 0,
        _ => request.CharId,
    };

    // Structural checks that need no database: no row touched twice, sane ids and amounts.
    private static bool IsWellFormed(InventoryOperationRequest request, out int badIndex)
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

    private static InventoryOperationResponse Respond(InventoryOperationStatus status, InventoryOperationRequest request, int failedIndex) =>
        new(status, request.CharId, request.OperationId, failedIndex, []);

    // SQL Server 2627 (PK violation) / 2601 (unique index) anywhere in the exception chain.
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => error.Number is 2627 or 2601)) return true;
        return false;
    }
}
