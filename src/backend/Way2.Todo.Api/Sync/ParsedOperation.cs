using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Api.Sync;

/// <summary>
/// Operação de push já convertida e normalizada, com o hash da sua representação canônica.
/// Regras de conteúdo (título, categoria, limites) continuam no <see cref="TaskCommandService"/>.
/// </summary>
public sealed record ParsedOperation(
    Guid OperationId,
    Guid TaskId,
    string Action,
    int? BaseVersion,
    TaskFields? Fields,
    DateTime? CreatedAt,
    string RequestHash)
{
    public bool IsCreate => Action == SyncActions.Upsert && BaseVersion is null;

    public static ParsedOperation? Parse(PushOperation operation, out Dictionary<string, string[]> errors)
    {
        errors = [];
        if (operation.OperationId == Guid.Empty)
            errors["operationId"] = ["operationId é obrigatório."];
        if (operation.TaskId == Guid.Empty)
            errors["taskId"] = ["taskId é obrigatório."];
        if (operation.BaseVersion is < 1)
            errors["baseVersion"] = ["baseVersion deve ser maior ou igual a 1."];

        TaskFields? fields = null;
        DateTime? createdAt = null;
        // Despacha pela action primeiro: um delete sem baseVersion nunca é interpretado como criação.
        switch (operation.Action)
        {
            case SyncActions.Delete:
                if (operation.BaseVersion is null)
                    errors["baseVersion"] = ["Exclusão exige baseVersion."];
                break;
            case SyncActions.Upsert when operation.Payload is null:
                errors["payload"] = ["Payload é obrigatório em upsert."];
                break;
            case SyncActions.Upsert:
                (fields, createdAt) = ParsePayload(operation.Payload, operation.BaseVersion is null, errors);
                break;
            default:
                errors["action"] = ["action deve ser upsert ou delete."];
                break;
        }
        if (errors.Count > 0)
            return null;

        fields = fields?.Normalize();
        var hash = HashOf(operation.TaskId, operation.Action!, operation.BaseVersion, fields, createdAt);
        return new ParsedOperation(
            operation.OperationId, operation.TaskId, operation.Action!, operation.BaseVersion, fields, createdAt, hash);
    }

    private static (TaskFields?, DateTime?) ParsePayload(
        PushPayload payload, bool creating, Dictionary<string, string[]> errors)
    {
        if (!TryParseName(payload.Priority, out TaskPriority priority))
            errors["priority"] = ["priority deve ser low, medium ou high."];
        if (!TryParseName(payload.Status, out TodoTaskStatus status))
            errors["status"] = ["status deve ser pending ou completed."];

        DateOnly? dueDate = null;
        if (!string.IsNullOrEmpty(payload.DueDate))
        {
            if (DateOnly.TryParseExact(payload.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                dueDate = date;
            else
                errors["dueDate"] = ["dueDate deve ser uma data YYYY-MM-DD."];
        }

        DateTime? createdAt = null;
        if (payload.CreatedAt is not null)
        {
            if (DateTime.TryParse(payload.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
                && instant.Kind == DateTimeKind.Utc)
                createdAt = UtcTimestamp.Truncate(instant);
            else
                errors["createdAt"] = ["createdAt deve ser um instante UTC ISO 8601 (sufixo Z)."];
        }
        else if (creating)
        {
            errors["createdAt"] = ["createdAt é obrigatório na criação."];
        }

        var fields = new TaskFields(payload.Title, payload.Description, dueDate, priority, status, payload.Category);
        return (fields, createdAt);
    }

    /// <summary>Aceita só o nome em minúsculas do contrato (rejeita números e outras grafias).</summary>
    private static bool TryParseName<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        return value is { Length: > 0 }
            && value.All(char.IsAsciiLetterLower)
            && Enum.TryParse(value, ignoreCase: true, out result)
            && Enum.IsDefined(result);
    }

    /// <summary>
    /// SHA-256 da operação canônica: campos normalizados, enums em minúsculas, datas em formato fixo.
    /// Retries idênticos produzem o mesmo hash; um <c>operationId</c> reutilizado com outro conteúdo, não.
    /// </summary>
    private static string HashOf(Guid taskId, string action, int? baseVersion, TaskFields? fields, DateTime? createdAt)
    {
        var payload = fields is null
            ? null
            : new CanonicalPayload(
                fields.Title,
                fields.Description,
                fields.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                fields.Priority,
                fields.Status,
                fields.Category,
                createdAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        var canonical = JsonSerializer.Serialize(new CanonicalOperation(taskId, action, baseVersion, payload), SyncJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record CanonicalOperation(Guid TaskId, string Action, int? BaseVersion, CanonicalPayload? Payload);

    private sealed record CanonicalPayload(
        string? Title,
        string? Description,
        string? DueDate,
        TaskPriority Priority,
        TodoTaskStatus Status,
        string? Category,
        string? CreatedAt);
}
