namespace Way2.Todo.Api.Sync;

/// <summary>
/// Última revisão de sincronização de um usuário. A linha é travada (<c>SELECT … FOR UPDATE</c>) no início
/// de cada operação de push: isso serializa as escritas do usuário e mantém o cursor monotônico.
/// </summary>
public sealed class UserSyncCounter
{
    public Guid UserId { get; init; }
    public long LastRevision { get; private set; }

    public long Advance() => ++LastRevision;
}

/// <summary>
/// Operação aceita (<c>applied</c>). Um retry do mesmo <c>operationId</c> devolve o resultado guardado aqui,
/// desde que o conteúdo (<see cref="RequestHash"/>) seja idêntico.
/// </summary>
public sealed class ProcessedOperation
{
    public Guid UserId { get; init; }
    public Guid OperationId { get; init; }
    public Guid TaskId { get; init; }

    /// <summary>SHA-256 (hex) da representação canônica da operação recebida.</summary>
    public required string RequestHash { get; init; }

    /// <summary>Resultado original (<see cref="StoredOperationResult"/>) em JSON.</summary>
    public required string ResultJson { get; init; }

    public DateTime ProcessedAt { get; init; }
}

/// <summary>Mudança confirmada, na ordem de <see cref="Revision"/> por usuário (fonte do pull).</summary>
public sealed class TaskChangeLogEntry
{
    public Guid UserId { get; init; }
    public long Revision { get; init; }
    public Guid TaskId { get; init; }

    /// <summary><c>upsert</c> ou <c>delete</c> (tombstone).</summary>
    public required string ChangeType { get; init; }

    /// <summary>Snapshot da tarefa após a mudança, em JSON (inclui <c>deletedAt</c> quando excluída).</summary>
    public required string SnapshotJson { get; init; }

    public DateTime CreatedAt { get; init; }
}
