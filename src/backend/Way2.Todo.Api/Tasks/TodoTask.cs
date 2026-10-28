namespace Way2.Todo.Api.Tasks;

/// <summary>
/// Tarefa de um usuário. O id é gerado no cliente (compatível com criação offline);
/// <see cref="Version"/> começa em 1 e incrementa a cada alteração aceita;
/// exclusão é lógica (<see cref="DeletedAt"/> funciona como tombstone).
/// </summary>
public sealed class TodoTask
{
    private TodoTask() { } // EF Core

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public TaskPriority Priority { get; private set; }
    public TodoTaskStatus Status { get; private set; }
    public string Category { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    /// <param name="createdAt">Instante UTC informado pelo cliente (a tarefa pode ter nascido offline).</param>
    public static TodoTask Create(Guid id, Guid userId, TaskFields fields, DateTime createdAt, DateTime now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id da tarefa é obrigatório.", nameof(id));
        if (userId == Guid.Empty) throw new ArgumentException("Usuário é obrigatório.", nameof(userId));
        if (createdAt.Kind != DateTimeKind.Utc) throw new ArgumentException("createdAt deve ser UTC.", nameof(createdAt));

        var task = new TodoTask
        {
            Id = id,
            UserId = userId,
            Version = 1,
            CreatedAt = UtcTimestamp.Truncate(createdAt),
            UpdatedAt = UtcTimestamp.Truncate(now),
        };
        task.Apply(fields);
        return task;
    }

    public void Update(TaskFields fields, DateTime now)
    {
        EnsureNotDeleted();
        Apply(fields);
        Version++;
        UpdatedAt = UtcTimestamp.Truncate(now);
    }

    public void SoftDelete(DateTime now)
    {
        EnsureNotDeleted();
        Version++;
        UpdatedAt = UtcTimestamp.Truncate(now);
        DeletedAt = UpdatedAt;
    }

    private void Apply(TaskFields fields)
    {
        var normalized = fields.Normalize();
        if (normalized.Validate().Count > 0)
            throw new ArgumentException("Campos de tarefa inválidos; valide antes de aplicar.", nameof(fields));

        Title = normalized.Title!;
        Description = normalized.Description;
        DueDate = normalized.DueDate;
        Priority = normalized.Priority;
        Status = normalized.Status;
        Category = normalized.Category!;
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted) throw new InvalidOperationException("Tarefa excluída não pode ser alterada.");
    }
}

public static class UtcTimestamp
{
    /// <summary>
    /// Trunca em milissegundos: mesma precisão do JavaScript, e o valor sobrevive
    /// sem alteração ao round-trip pelo PostgreSQL (precisão de microssegundos).
    /// </summary>
    public static DateTime Truncate(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
}
