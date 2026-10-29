using System.Text.Json;
using System.Text.Json.Serialization;
using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Api.Integrations;

/// <summary>
/// Evento de integração da Transactional Outbox. Nasce no MESMO commit da mudança aplicada
/// (tarefa + change log + processed_operations) e é entregue depois, fora da transação, por webhook.
/// </summary>
public sealed class OutboxEvent
{
    public Guid EventId { get; init; }
    public Guid UserId { get; init; }
    public Guid TaskId { get; init; }
    public required string EventType { get; init; }
    public int SchemaVersion { get; init; }
    public int TaskVersion { get; init; }
    public long ServerRevision { get; init; }
    public DateTime OccurredAt { get; init; }

    /// <summary>Envelope JSON entregue ao consumidor (os mesmos bytes são assinados e enviados).</summary>
    public required string PayloadJson { get; init; }

    public string Status { get; private set; } = OutboxStatuses.Pending;
    public int Attempts { get; private set; }
    public DateTime NextRetryAt { get; init; }
    public DateTime? LockedUntil { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTime? LastAttemptAt { get; private set; }
    public int? LastStatusCode { get; private set; }

    /// <summary>Só o tipo da falha (status HTTP, timeout, conexão) — nunca corpo, URL ou segredo.</summary>
    public string? LastError { get; private set; }

    public DateTime? DeliveredAt { get; private set; }
}

public static class OutboxStatuses
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
}

public static class TaskEventTypes
{
    public const string Created = "task.created";
    public const string Updated = "task.updated";
    public const string Deleted = "task.deleted";
}

/// <summary>Contrato v1 do evento (docs/SYNC_CONTRACT.md §6).</summary>
public sealed record TaskEventEnvelope(
    int SchemaVersion,
    Guid EventId,
    string EventType,
    DateTime OccurredAt,
    Guid UserId,
    Guid TaskId,
    int TaskVersion,
    long ServerRevision,
    TaskSnapshot Task);

public static class OutboxEvents
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Evento de uma mudança aplicada no push; <paramref name="now"/> é o instante do commit.</summary>
    public static OutboxEvent ForAppliedChange(
        Guid userId, long serverRevision, TaskSnapshot task, string eventType, DateTime now)
    {
        var eventId = Guid.CreateVersion7(new DateTimeOffset(now));
        var envelope = new TaskEventEnvelope(
            SchemaVersion, eventId, eventType, now, userId, task.Id, task.Version, serverRevision, task);
        return new OutboxEvent
        {
            EventId = eventId,
            UserId = userId,
            TaskId = task.Id,
            EventType = eventType,
            SchemaVersion = SchemaVersion,
            TaskVersion = task.Version,
            ServerRevision = serverRevision,
            OccurredAt = now,
            PayloadJson = JsonSerializer.Serialize(envelope, Json),
            NextRetryAt = now,
        };
    }
}
