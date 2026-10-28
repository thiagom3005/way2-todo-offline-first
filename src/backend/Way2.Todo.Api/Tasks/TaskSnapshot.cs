namespace Way2.Todo.Api.Tasks;

/// <summary>Representação pública de uma tarefa (docs/SYNC_CONTRACT.md, "Modelo de tarefa").</summary>
public sealed record TaskSnapshot(
    Guid Id,
    string Title,
    string? Description,
    DateOnly? DueDate,
    TaskPriority Priority,
    TodoTaskStatus Status,
    string Category,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt)
{
    public static TaskSnapshot From(TodoTask task) => new(
        task.Id,
        task.Title,
        task.Description,
        task.DueDate,
        task.Priority,
        task.Status,
        task.Category,
        task.Version,
        task.CreatedAt,
        task.UpdatedAt,
        task.DeletedAt);
}
