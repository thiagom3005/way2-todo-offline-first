using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Way2.Todo.Consumer;

/// <summary>Evento v1 recebido (docs/SYNC_CONTRACT.md §6), já validado.</summary>
public sealed record IncomingEvent(
    Guid EventId,
    string EventType,
    Guid UserId,
    Guid TaskId,
    int TaskVersion,
    long ServerRevision,
    DateTimeOffset OccurredAt,
    JsonObject Task)
{
    private static readonly string[] EventTypes = ["task.created", "task.updated", "task.deleted"];

    public static IncomingEvent? TryParse(byte[] body, string? eventIdHeader)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject json || json["schemaVersion"]?.GetValue<int>() != 1)
                return null;
            var incoming = new IncomingEvent(
                json["eventId"]!.GetValue<Guid>(),
                json["eventType"]!.GetValue<string>(),
                json["userId"]!.GetValue<Guid>(),
                json["taskId"]!.GetValue<Guid>(),
                json["taskVersion"]!.GetValue<int>(),
                json["serverRevision"]!.GetValue<long>(),
                json["occurredAt"]!.GetValue<DateTimeOffset>(),
                json["task"]!.AsObject());
            var consistent = EventTypes.Contains(incoming.EventType)
                && eventIdHeader == incoming.EventId.ToString()
                && incoming.Task["id"]?.GetValue<Guid>() == incoming.TaskId;
            return consistent ? incoming : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }
}

public sealed record StoredEvent(
    Guid EventId,
    string EventType,
    Guid UserId,
    Guid TaskId,
    int TaskVersion,
    long ServerRevision,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    int Deliveries);

/// <summary>Representação local (somente leitura) das tarefas, montada a partir dos eventos.</summary>
public sealed record TaskView(
    Guid TaskId,
    Guid UserId,
    string? Title,
    string? Status,
    string? Category,
    int Version,
    bool Deleted,
    Guid LastEventId,
    DateTimeOffset OccurredAt);

public sealed record EventsSnapshot(int TotalDeliveries, int UniqueEvents, int DuplicateDeliveries, IReadOnlyList<StoredEvent> Events);

/// <summary>
/// Registro dos webhooks recebidos: log JSONL append-only (fonte da verdade), com índice de deduplicação
/// e projeção reconstruídos a partir dele ao iniciar. Checagem, gravação em disco (fsync) e atualização
/// do índice acontecem sob um único lock: um evento só conta como recebido depois de durável.
/// </summary>
public sealed class EventStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly Dictionary<Guid, StoredEvent> events = [];
    private readonly Dictionary<Guid, TaskView> tasks = [];
    private readonly FileStream file;
    private int deliveries;

    public EventStore(string path, ILogger<EventStore> logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var validLength = Load(path, logger);
        file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (file.Length > validLength)
        {
            logger.LogWarning("Última linha do log truncada (gravação interrompida) removida ao iniciar.");
            file.SetLength(validLength);
        }
        file.Seek(0, SeekOrigin.End);
    }

    public bool Accept(IncomingEvent incoming, DateTimeOffset receivedAt, out StoredEvent stored)
    {
        lock (gate)
        {
            if (events.TryGetValue(incoming.EventId, out var existing))
            {
                Append(new JsonObject
                {
                    ["kind"] = "duplicate",
                    ["eventId"] = incoming.EventId,
                    ["receivedAt"] = receivedAt,
                });
                stored = existing with { Deliveries = existing.Deliveries + 1 };
                events[incoming.EventId] = stored;
                deliveries++;
                return false;
            }

            Append(new JsonObject
            {
                ["kind"] = "event",
                ["eventId"] = incoming.EventId,
                ["eventType"] = incoming.EventType,
                ["userId"] = incoming.UserId,
                ["taskId"] = incoming.TaskId,
                ["taskVersion"] = incoming.TaskVersion,
                ["serverRevision"] = incoming.ServerRevision,
                ["occurredAt"] = incoming.OccurredAt,
                ["receivedAt"] = receivedAt,
                ["task"] = incoming.Task.DeepClone(),
            });
            stored = Index(incoming, receivedAt);
            return true;
        }
    }

    public EventsSnapshot Events()
    {
        lock (gate)
        {
            var list = events.Values.OrderByDescending(e => e.ReceivedAt).ToList();
            return new EventsSnapshot(deliveries, events.Count, deliveries - events.Count, list);
        }
    }

    public IReadOnlyList<TaskView> Tasks()
    {
        lock (gate)
        {
            return [.. tasks.Values.OrderByDescending(t => t.OccurredAt)];
        }
    }

    public void Dispose() => file.Dispose();

    private StoredEvent Index(IncomingEvent incoming, DateTimeOffset receivedAt)
    {
        var stored = new StoredEvent(
            incoming.EventId, incoming.EventType, incoming.UserId, incoming.TaskId, incoming.TaskVersion,
            incoming.ServerRevision, incoming.OccurredAt, receivedAt, Deliveries: 1);
        events[incoming.EventId] = stored;
        deliveries++;

        // Guarda de versão: entrega atrasada ou fora de ordem nunca sobrescreve um estado mais novo.
        // Tarefa desconhecida é criada mesmo num task.updated (eventos anteriores à integração não existem).
        if (!tasks.TryGetValue(incoming.TaskId, out var current) || incoming.TaskVersion > current.Version)
        {
            tasks[incoming.TaskId] = new TaskView(
                incoming.TaskId,
                incoming.UserId,
                incoming.Task["title"]?.GetValue<string>(),
                incoming.Task["status"]?.GetValue<string>(),
                incoming.Task["category"]?.GetValue<string>(),
                incoming.TaskVersion,
                Deleted: incoming.EventType == "task.deleted" || incoming.Task["deletedAt"] is not null,
                incoming.EventId,
                incoming.OccurredAt);
        }
        return stored;
    }

    private void Append(JsonObject line)
    {
        var bytes = Encoding.UTF8.GetBytes(line.ToJsonString(Json) + "\n");
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    /// <summary>Reconstrói índice e projeção; devolve o tamanho do trecho válido do arquivo.</summary>
    private long Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
            return 0;
        var content = File.ReadAllBytes(path);
        long offset = 0;
        var duplicates = 0;
        while (offset < content.Length)
        {
            var end = Array.IndexOf(content, (byte)'\n', (int)offset);
            if (end < 0)
                break; // última linha sem quebra: gravação interrompida, descartada
            var line = JsonNode.Parse(content.AsSpan((int)offset, end - (int)offset))?.AsObject()
                ?? throw new InvalidDataException($"Log de eventos corrompido no byte {offset}.");
            var eventId = line["eventId"]!.GetValue<Guid>();
            if (line["kind"]!.GetValue<string>() == "duplicate")
            {
                var existing = events[eventId];
                events[eventId] = existing with { Deliveries = existing.Deliveries + 1 };
                deliveries++;
                duplicates++;
            }
            else
            {
                Index(new IncomingEvent(
                    eventId,
                    line["eventType"]!.GetValue<string>(),
                    line["userId"]!.GetValue<Guid>(),
                    line["taskId"]!.GetValue<Guid>(),
                    line["taskVersion"]!.GetValue<int>(),
                    line["serverRevision"]!.GetValue<long>(),
                    line["occurredAt"]!.GetValue<DateTimeOffset>(),
                    line["task"]!.AsObject().DeepClone().AsObject()),
                    line["receivedAt"]!.GetValue<DateTimeOffset>());
            }
            offset = end + 1;
        }
        logger.LogInformation(
            "Log de eventos carregado: {Events} eventos, {Duplicates} entregas duplicadas.", events.Count, duplicates);
        return offset;
    }
}
