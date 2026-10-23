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
        if (!InventoryOperationEngine.IsWellFormed(request, out var badIndex)) return Respond(InventoryOperationStatus.Invalid, request, badIndex);

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

        // Tracked (not AsNoTracking): a ZenyCost > 0 operation mutates this same entity's Zeny below, in the SAME
        // SaveChangesAsync pass as the row mutations and the operation-log row - one transaction, one commit.
        CharCharacter? character = null;
        if (request.ZenyCost > 0)
        {
            character = await db.Characters.FirstOrDefaultAsync(c => c.CharId == request.CharId && c.AccountId == request.AccountId, cancellationToken);
            if (character is null) return Respond(InventoryOperationStatus.Invalid, request, -1);
        }

        // Load the rows the operation names (one query per container), let the shared engine decide, then persist its changes.
        var tracked = new Dictionary<(InventoryContainer Container, uint Id), IItemRowColumns>();
        foreach (var group in request.Mutations.Where(m => m is not AddNewRowMutation).GroupBy(m => m.Container))
        {
            var ids = group.Select(DurableIdOf).ToArray();
            var owner = OwnerOf(group.Key, request, guildId);
            IReadOnlyList<IItemRowColumns> rows = group.Key == InventoryContainer.Inventory
                ? await db.Inventory.Where(i => i.CharId == owner && ids.Contains(i.Id)).ToListAsync(cancellationToken)
                : await db.ContainerItems.Where(i => i.Container == (byte)group.Key && i.OwnerId == owner && ids.Contains(i.Id)).ToListAsync(cancellationToken);
            foreach (var row in rows) tracked[(group.Key, row.Id)] = row;
        }

        var evaluation = InventoryOperationEngine.Evaluate(request, (container, id) => tracked.TryGetValue((container, id), out var entity) ? ItemRowMapper.ToRow(entity) : null, character?.Zeny ?? 0);
        if (evaluation.Status != InventoryOperationStatus.Applied) return Respond(evaluation.Status, request, evaluation.FailedIndex);

        // The engine has already verified character.Zeny == request.ExpectedZeny and >= request.ZenyCost; applying
        // the deduction here, before the first SaveChangesAsync, means it commits atomically with every row change
        // and the operation-log row below - a stale-write race on THIS row would surface as a normal EF
        // DbUpdateConcurrencyException/SQL error on save, caught by the same handlers as every other row conflict.
        if (character is not null) character.Zeny -= request.ZenyCost;

        var touched = new List<(IItemRowColumns Entity, bool Deleted, uint DurableId)>();
        foreach (var change in evaluation.Changes)
        {
            switch (change.Kind)
            {
                case InventoryRowChangeKind.Insert:
                {
                    IItemRowColumns entity = change.Container == InventoryContainer.Inventory
                        ? new CharInventory { CharId = request.CharId }
                        : new CharContainerItem { Container = (byte)change.Container, OwnerId = OwnerOf(change.Container, request, guildId) };
                    ItemRowMapper.ApplyNew(entity, change.Row);
                    entity.Revision = change.Row.Revision;
                    db.Add(entity);
                    touched.Add((entity, false, 0));
                    break;
                }
                case InventoryRowChangeKind.Update:
                {
                    var entity = tracked[(change.Container, change.Row.DurableId)];
                    ItemRowMapper.ApplyMutable(entity, change.Row);
                    entity.Amount = change.Row.Amount;
                    entity.Revision = change.Row.Revision;
                    touched.Add((entity, false, entity.Id));
                    break;
                }
                case InventoryRowChangeKind.Delete:
                {
                    var entity = tracked[(change.Container, change.Row.DurableId)];
                    db.Remove(entity);
                    touched.Add((entity, true, entity.Id));
                    break;
                }
            }
        }

        // First save: the item mutations (assigns generated ids; row-revision conflicts surface here).
        await db.SaveChangesAsync(cancellationToken);

        var results = touched.Select(t => new InventoryMutationResult(t.Deleted ? t.DurableId : t.Entity.Id, t.Deleted, t.Deleted ? null : ItemRowMapper.ToRow(t.Entity))).ToList();
        var response = new InventoryOperationResponse(InventoryOperationStatus.Applied, request.CharId, request.OperationId, -1, results, character?.Zeny ?? 0);

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

    private static uint OwnerOf(InventoryContainer container, InventoryOperationRequest request, uint? guildId) => container switch
    {
        InventoryContainer.Storage => request.AccountId,
        InventoryContainer.GuildStorage => guildId ?? 0,
        _ => request.CharId,
    };

    private static uint DurableIdOf(InventoryMutation mutation) => mutation switch
    {
        AddToStackMutation stack => stack.DurableId,
        RemoveFromRowMutation remove => remove.DurableId,
        UpdateRowMutation update => update.DurableId,
        _ => 0,
    };

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
