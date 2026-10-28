using Microsoft.EntityFrameworkCore;
using Npgsql;
using Way2.Todo.Api.Infrastructure;

namespace Way2.Todo.Api.Tasks;

/// <summary>Resultado de um comando, alinhado aos status do contrato de sync.</summary>
public abstract record TaskCommandResult
{
    private TaskCommandResult() { }

    /// <summary>Comando aceito; <paramref name="Task"/> é o novo snapshot.</summary>
    public sealed record Applied(TaskSnapshot Task) : TaskCommandResult;

    /// <summary>
    /// Comando recusado sem alterar nada. <paramref name="ServerTask"/> só é preenchido quando a tarefa
    /// pertence ao próprio usuário — nunca expõe dados de outra conta.
    /// </summary>
    public sealed record Conflict(string Reason, TaskSnapshot? ServerTask) : TaskCommandResult;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : TaskCommandResult;
}

public static class ConflictReasons
{
    public const string VersionMismatch = "version_mismatch";
    public const string Deleted = "deleted";
    public const string AlreadyExists = "already_exists";
    public const string NotFound = "not_found";
}

/// <summary>
/// Criação, alteração e exclusão lógica de tarefas, sempre no escopo do usuário autenticado e com
/// controle otimista por <c>baseVersion</c>. Na Fase B será chamado pelo <c>POST /api/sync/push</c>
/// dentro da transação que também grava processed_operations e task_change_log.
/// </summary>
public sealed class TaskCommandService(TodoDbContext db, TimeProvider clock)
{
    public async Task<TaskCommandResult> CreateAsync(
        Guid userId, Guid taskId, TaskFields fields, DateTime createdAt, CancellationToken ct = default)
    {
        fields = fields.Normalize();
        var errors = fields.Validate();
        if (taskId == Guid.Empty)
            errors["taskId"] = ["Id da tarefa é obrigatório."];
        if (createdAt.Kind != DateTimeKind.Utc)
            errors["createdAt"] = ["createdAt deve ser um instante UTC ISO 8601."];
        if (errors.Count > 0)
            return new TaskCommandResult.Invalid(errors);

        var existing = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (existing is not null)
            return AlreadyExists(existing, userId);

        var task = TodoTask.Create(taskId, userId, fields, createdAt, Now());
        db.Tasks.Add(task);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Outra requisição criou o mesmo id entre a checagem e o INSERT.
            db.Entry(task).State = EntityState.Detached;
            existing = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId, ct);
            return AlreadyExists(existing, userId);
        }

        return new TaskCommandResult.Applied(TaskSnapshot.From(task));
    }

    public async Task<TaskCommandResult> UpdateAsync(
        Guid userId, Guid taskId, int baseVersion, TaskFields fields, CancellationToken ct = default)
    {
        fields = fields.Normalize();
        var errors = fields.Validate();
        if (errors.Count > 0)
            return new TaskCommandResult.Invalid(errors);

        var task = await FindOwnedAsync(userId, taskId, ct);
        if (task is null)
            return new TaskCommandResult.Conflict(ConflictReasons.NotFound, null);
        if (RejectStale(task, baseVersion) is { } conflict)
            return conflict;

        task.Update(fields, Now());
        return await SaveAsync(task, ct);
    }

    public async Task<TaskCommandResult> DeleteAsync(
        Guid userId, Guid taskId, int baseVersion, CancellationToken ct = default)
    {
        var task = await FindOwnedAsync(userId, taskId, ct);
        if (task is null)
            return new TaskCommandResult.Conflict(ConflictReasons.NotFound, null);
        if (RejectStale(task, baseVersion) is { } conflict)
            return conflict;

        task.SoftDelete(Now());
        return await SaveAsync(task, ct);
    }

    private Task<TodoTask?> FindOwnedAsync(Guid userId, Guid taskId, CancellationToken ct) =>
        db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == userId, ct);

    private static TaskCommandResult.Conflict? RejectStale(TodoTask task, int baseVersion)
    {
        if (task.IsDeleted)
            return new TaskCommandResult.Conflict(ConflictReasons.Deleted, TaskSnapshot.From(task));
        if (task.Version != baseVersion)
            return new TaskCommandResult.Conflict(ConflictReasons.VersionMismatch, TaskSnapshot.From(task));
        return null;
    }

    private async Task<TaskCommandResult> SaveAsync(TodoTask task, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return new TaskCommandResult.Applied(TaskSnapshot.From(task));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Outra escrita mudou a versão depois da nossa leitura: devolve o estado atual, sem sobrescrever.
            await db.Entry(task).ReloadAsync(ct);
            return new TaskCommandResult.Conflict(ConflictReasons.VersionMismatch, TaskSnapshot.From(task));
        }
    }

    private static TaskCommandResult AlreadyExists(TodoTask existing, Guid userId) =>
        new TaskCommandResult.Conflict(
            ConflictReasons.AlreadyExists,
            existing.UserId == userId ? TaskSnapshot.From(existing) : null);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
