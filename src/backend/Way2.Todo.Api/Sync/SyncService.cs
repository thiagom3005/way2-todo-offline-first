using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Infrastructure;
using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Api.Sync;

/// <summary>
/// Push e pull do contrato de sincronização. Cada operação aceita grava, numa única transação PostgreSQL,
/// a tarefa (via <see cref="TaskCommandService"/>), a revisão do usuário, o change log e
/// <c>processed_operations</c>. A Fase C acrescenta <c>integration_outbox</c> nesta mesma transação.
/// </summary>
public sealed class SyncService(
    TodoDbContext db, TaskCommandService tasks, TimeProvider clock, ILogger<SyncService> logger)
{
    private static readonly Dictionary<string, string[]> ReusedOperationId = new()
    {
        ["operationId"] = ["operationId já utilizado com outro conteúdo."],
    };

    public async Task<PushResponse> PushAsync(Guid userId, PushRequest request, CancellationToken ct = default)
    {
        var results = new List<PushResult>(request.Operations!.Count);
        // Ordem recebida, uma transação por operação: erro de negócio numa não invalida as demais.
        foreach (var operation in request.Operations!)
        {
            var parsed = ParsedOperation.Parse(operation, out var errors);
            var result = parsed is null
                ? PushResult.Invalid(operation.OperationId, errors)
                : await ProcessAsync(userId, parsed, ct);
            if (result.Status == SyncStatuses.Conflict)
                logger.LogInformation(
                    "Conflito {Reason} na operação {OperationId} da tarefa {TaskId}",
                    result.Reason, operation.OperationId, operation.TaskId);
            results.Add(result);
        }

        logger.LogInformation(
            "Push do dispositivo {DeviceId}: {Total} operações, {Applied} aplicadas, {Duplicate} duplicadas, {Conflict} conflitos, {Invalid} inválidas",
            request.DeviceId, results.Count,
            results.Count(r => r.Status == SyncStatuses.Applied),
            results.Count(r => r.Status == SyncStatuses.Duplicate),
            results.Count(r => r.Status == SyncStatuses.Conflict),
            results.Count(r => r.Status == SyncStatuses.ValidationError));
        return new PushResponse(results);
    }

    public async Task<PullResponse> PullAsync(Guid userId, long cursor, int limit, CancellationToken ct = default)
    {
        // Revisões de um usuário são atribuídas sob o lock do contador e confirmadas em ordem:
        // se a revisão N está visível, todas as anteriores também estão.
        var rows = await db.TaskChangeLog.AsNoTracking()
            .Where(c => c.UserId == userId && c.Revision > cursor)
            .OrderBy(c => c.Revision)
            .Take(limit + 1)
            .ToListAsync(ct);

        var changes = rows.Take(limit)
            .Select(c => new PullChange(c.Revision, c.ChangeType, Deserialize<TaskSnapshot>(c.SnapshotJson)))
            .ToList();
        var nextCursor = changes.Count > 0 ? changes[^1].Revision : cursor;
        return new PullResponse(changes, nextCursor, rows.Count > limit);
    }

    private async Task<PushResult> ProcessAsync(Guid userId, ParsedOperation operation, CancellationToken ct)
    {
        // Um rollback não desfaz entidades rastreadas: cada operação começa com o rastreador limpo.
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // 1) Lock por usuário ANTES da checagem de duplicidade: um retry concorrente do mesmo
        //    operationId espera aqui e, ao seguir, já enxerga a operação gravada (Read Committed).
        var counter = await LockCounterAsync(userId, ct);

        // 2) Idempotência: mesmo operationId e mesmo conteúdo devolvem o resultado original.
        var processed = await db.ProcessedOperations.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.OperationId == operation.OperationId, ct);
        if (processed is not null)
        {
            return processed.TaskId == operation.TaskId && processed.RequestHash == operation.RequestHash
                ? PushResult.Duplicate(operation.OperationId, Deserialize<StoredOperationResult>(processed.ResultJson))
                : PushResult.Invalid(operation.OperationId, ReusedOperationId);
        }

        // 3) baseVersion + mutação. Conflito ou validação: nada é gravado (rollback ao sair do using).
        var outcome = await ExecuteAsync(userId, operation, ct);
        if (outcome is not TaskCommandResult.Applied applied)
        {
            return outcome switch
            {
                TaskCommandResult.Conflict conflict => PushResult.Conflict(operation.OperationId, conflict.Reason, conflict.ServerTask),
                TaskCommandResult.Invalid invalid => PushResult.Invalid(operation.OperationId, invalid.Errors),
                _ => throw new InvalidOperationException($"Resultado inesperado: {outcome}"),
            };
        }

        // 4) Revisão, change log e operação processada no mesmo commit da tarefa.
        var now = clock.GetUtcNow().UtcDateTime;
        var stored = new StoredOperationResult(counter.Advance(), applied.Task);
        db.TaskChangeLog.Add(new TaskChangeLogEntry
        {
            UserId = userId,
            Revision = stored.ServerRevision,
            TaskId = applied.Task.Id,
            ChangeType = applied.Task.DeletedAt is null ? SyncActions.Upsert : SyncActions.Delete,
            SnapshotJson = JsonSerializer.Serialize(applied.Task, SyncJson.Options),
            CreatedAt = now,
        });
        db.ProcessedOperations.Add(new ProcessedOperation
        {
            UserId = userId,
            OperationId = operation.OperationId,
            TaskId = operation.TaskId,
            RequestHash = operation.RequestHash,
            ResultJson = JsonSerializer.Serialize(stored, SyncJson.Options),
            ProcessedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return PushResult.Applied(operation.OperationId, stored);
    }

    private Task<TaskCommandResult> ExecuteAsync(Guid userId, ParsedOperation operation, CancellationToken ct) =>
        operation switch
        {
            { Action: SyncActions.Delete } => tasks.DeleteAsync(userId, operation.TaskId, operation.BaseVersion!.Value, ct),
            { IsCreate: true } => tasks.CreateAsync(userId, operation.TaskId, operation.Fields!, operation.CreatedAt!.Value, ct),
            _ => tasks.UpdateAsync(userId, operation.TaskId, operation.BaseVersion!.Value, operation.Fields!, ct),
        };

    private async Task<UserSyncCounter> LockCounterAsync(Guid userId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO user_sync_counters (user_id, last_revision) VALUES ({userId}, 0) ON CONFLICT (user_id) DO NOTHING",
            ct);
        var counters = await db.SyncCounters
            .FromSql($"SELECT * FROM user_sync_counters WHERE user_id = {userId} FOR UPDATE")
            .ToListAsync(ct);
        return counters.Single();
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SyncJson.Options)
        ?? throw new InvalidOperationException($"JSON vazio para {typeof(T).Name}.");
}
