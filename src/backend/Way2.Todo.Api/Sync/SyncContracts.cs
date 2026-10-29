using System.Text.Json;
using System.Text.Json.Serialization;
using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Api.Sync;

// Contratos HTTP de docs/SYNC_CONTRACT.md (§1 push, §2 pull).

public sealed record PushRequest(Guid DeviceId, IReadOnlyList<PushOperation?>? Operations);

public sealed record PushOperation(
    Guid OperationId,
    Guid TaskId,
    string? Action,
    int? BaseVersion,
    PushPayload? Payload);

/// <summary>
/// Campos de enumeração e data chegam como texto e são convertidos por operação: um valor inválido vira
/// <c>validation_error</c> só naquela operação, em vez de invalidar o lote inteiro.
/// </summary>
public sealed record PushPayload(
    string? Title,
    string? Description,
    string? DueDate,
    string? Priority,
    string? Status,
    string? Category,
    string? CreatedAt);

public sealed record PushResponse(IReadOnlyList<PushResult> Results);

public sealed record PushResult(
    Guid OperationId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ServerRevision,
    TaskSnapshot? ServerTask,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Errors)
{
    public static PushResult Applied(Guid operationId, StoredOperationResult result) =>
        new(operationId, SyncStatuses.Applied, result.ServerRevision, result.ServerTask, null, null);

    public static PushResult Duplicate(Guid operationId, StoredOperationResult original) =>
        new(operationId, SyncStatuses.Duplicate, original.ServerRevision, original.ServerTask, null, null);

    public static PushResult Conflict(Guid operationId, string reason, TaskSnapshot? serverTask) =>
        new(operationId, SyncStatuses.Conflict, null, serverTask, reason, null);

    public static PushResult Invalid(Guid operationId, IReadOnlyDictionary<string, string[]> errors) =>
        new(operationId, SyncStatuses.ValidationError, null, null, null, errors);
}

/// <summary>Resultado original de uma operação aplicada, guardado em <c>processed_operations</c>.</summary>
public sealed record StoredOperationResult(long ServerRevision, TaskSnapshot ServerTask);

public sealed record PullResponse(IReadOnlyList<PullChange> Changes, long NextCursor, bool HasMore);

public sealed record PullChange(long Revision, string Action, TaskSnapshot Task);

public static class SyncStatuses
{
    public const string Applied = "applied";
    public const string Duplicate = "duplicate";
    public const string Conflict = "conflict";
    public const string ValidationError = "validation_error";
}

public static class SyncActions
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";
}

public static class SyncLimits
{
    public const int MaxOperationsPerPush = 50;

    /// <summary>
    /// 1 MiB (o padrão do Kestrel é 30 MB). O maior push válido fica abaixo de ~700 KB: 50 operações com
    /// 2.250 caracteres de texto cada, mesmo no pior caso de escape JSON (6 bytes por caractere).
    /// </summary>
    public const int MaxPushBodyBytes = 1024 * 1024;
    public const int DefaultPullLimit = 100;
    public const int MaxPullLimit = 500;
}

/// <summary>Mesmas convenções JSON da API (camelCase, enums em minúsculas) para o que é gravado no banco.</summary>
public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
