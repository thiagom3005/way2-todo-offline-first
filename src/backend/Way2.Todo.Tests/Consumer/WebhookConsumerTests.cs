using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Way2.Todo.Consumer;
using Way2.Todo.Tests.Support;
using ApiSignature = Way2.Todo.Api.Integrations.WebhookSignature;

namespace Way2.Todo.Tests.Consumer;

public sealed class WebhookConsumerTests : IAsyncLifetime
{
    private const string Secret = "test-only-webhook-secret-with-32-bytes!!";
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"way2-consumer-{Guid.NewGuid():N}");
    private readonly List<ConsumerFactory> factories = [];

    private string DataPath => Path.Combine(directory, "events.jsonl");

    private HttpClient Start()
    {
        var factory = new ConsumerFactory(DataPath, Secret);
        factories.Add(factory);
        return factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        TempDirectory.TryDelete(directory);
    }

    /// <summary>Lê o log enquanto o consumidor o mantém aberto para append.</summary>
    private string[] LogLines()
    {
        using var stream = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Envelope(
        Guid eventId, string eventType, Guid taskId, int version, string title = "Tarefa", bool deleted = false) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventId,
            eventType,
            occurredAt = DateTime.UtcNow,
            userId = UserId,
            taskId,
            taskVersion = version,
            serverRevision = version,
            task = new
            {
                id = taskId,
                title,
                description = (string?)null,
                dueDate = (string?)null,
                priority = "medium",
                status = "pending",
                category = "Campo",
                version,
                createdAt = DateTime.UtcNow,
                updatedAt = DateTime.UtcNow,
                deletedAt = deleted ? DateTime.UtcNow : (DateTime?)null,
            },
        });

    private static Task<HttpResponseMessage> DeliverAsync(
        HttpClient client, Guid eventId, string body, long? timestamp = null, string secret = Secret,
        string? sentBody = null, bool signed = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/todo-events")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(sentBody ?? body)),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Way2-Event-Id", eventId.ToString());
        if (signed)
        {
            var t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            request.Headers.Add(ApiSignature.Header, ApiSignature.Create(secret, t, Encoding.UTF8.GetBytes(body)));
        }
        return client.SendAsync(request);
    }

    private static async Task<EventsSnapshot> EventsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<EventsSnapshot>("/events"))!;

    private static async Task<TaskView> TaskAsync(HttpClient client, Guid taskId) =>
        (await client.GetFromJsonAsync<List<TaskView>>("/tasks"))!.Single(t => t.TaskId == taskId);

    private static async Task<bool> DuplicateFlagAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean();

    [Fact]
    public async Task Accepts_a_signed_event_and_projects_the_task()
    {
        var client = Start();
        var (eventId, taskId) = (Guid.NewGuid(), Guid.NewGuid());

        var response = await DeliverAsync(client, eventId, Envelope(eventId, "task.created", taskId, 1, "Conferir medição"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await DuplicateFlagAsync(response));
        var events = await EventsAsync(client);
        Assert.Equal((1, 1, 0), (events.UniqueEvents, events.TotalDeliveries, events.DuplicateDeliveries));
        var task = await TaskAsync(client, taskId);
        Assert.Equal(("Conferir medição", 1, false), (task.Title, task.Version, task.Deleted));
    }

    [Fact]
    public async Task Rejects_invalid_signatures_without_storing_anything()
    {
        var client = Start();
        var (eventId, taskId) = (Guid.NewGuid(), Guid.NewGuid());
        var body = Envelope(eventId, "task.created", taskId, 1);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var responses = new[]
        {
            await DeliverAsync(client, eventId, body, sentBody: body.Replace("Tarefa", "Adulterada")),
            await DeliverAsync(client, eventId, body, secret: "another-secret-that-is-at-least-32-bytes"),
            // Margem folgada: o relógio do consumidor anda durante o teste, e "now + 301" vira 300 (válido) se
            // um segundo virar no meio. As bordas exatas estão em ConsumerSignatureWindowTests, com relógio fixo.
            await DeliverAsync(client, eventId, body, timestamp: now - 330),
            await DeliverAsync(client, eventId, body, timestamp: now + 330),
            // "now - t" daria exatamente long.MinValue: antes estourava em Math.Abs e virava 500.
            await DeliverAsync(client, eventId, body, timestamp: now + long.MinValue),
            await DeliverAsync(client, eventId, body, timestamp: long.MaxValue),
            await DeliverAsync(client, eventId, body, signed: false),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(0, (await EventsAsync(client)).TotalDeliveries);
    }

    [Fact]
    public async Task Duplicate_deliveries_including_concurrent_ones_store_a_single_event()
    {
        var client = Start();
        var (eventId, taskId) = (Guid.NewGuid(), Guid.NewGuid());
        var body = Envelope(eventId, "task.created", taskId, 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 11).Select(_ => DeliverAsync(client, eventId, body)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(10, (await Task.WhenAll(responses.Select(DuplicateFlagAsync))).Count(duplicate => duplicate));
        var events = await EventsAsync(client);
        Assert.Equal((1, 11, 10), (events.UniqueEvents, events.TotalDeliveries, events.DuplicateDeliveries));
        var lines = LogLines();
        Assert.Single(lines, l => l.Contains("\"kind\":\"event\""));
    }

    [Fact]
    public async Task A_lost_ack_is_answered_as_duplicate_on_retry()
    {
        var client = Start();
        var (eventId, taskId) = (Guid.NewGuid(), Guid.NewGuid());
        var body = Envelope(eventId, "task.created", taskId, 1);
        await client.PutAsJsonAsync("/failure-mode", new { statusCode = 500, remaining = 1, afterProcessing = true });

        var lost = await DeliverAsync(client, eventId, body);
        var retry = await DeliverAsync(client, eventId, body);

        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(await DuplicateFlagAsync(retry));
        Assert.Equal(1, (await EventsAsync(client)).UniqueEvents);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/failure-mode")).StatusCode);
    }

    [Fact]
    public async Task Simulated_failures_respond_without_storing_the_event()
    {
        var client = Start();
        var (eventId, taskId) = (Guid.NewGuid(), Guid.NewGuid());
        var body = Envelope(eventId, "task.created", taskId, 1);
        await client.PutAsJsonAsync("/failure-mode", new { statusCode = 503, remaining = 2 });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add((await DeliverAsync(client, eventId, body)).StatusCode);

        Assert.Equal([HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK], statuses);
        var events = await EventsAsync(client);
        Assert.Equal((1, 1), (events.UniqueEvents, events.TotalDeliveries));
    }

    [Fact]
    public async Task Version_guard_keeps_the_newest_state_and_marks_deletes()
    {
        var client = Start();
        var taskId = Guid.NewGuid();
        var (v1, v2, v3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        // task.updated de tarefa desconhecida (sem backfill) cria a tarefa; o v1 atrasado não regride.
        await DeliverAsync(client, v2, Envelope(v2, "task.updated", taskId, 2, "Versão 2"));
        await DeliverAsync(client, v1, Envelope(v1, "task.created", taskId, 1, "Versão 1"));
        var afterLateEvent = await TaskAsync(client, taskId);
        await DeliverAsync(client, v3, Envelope(v3, "task.deleted", taskId, 3, "Versão 2", deleted: true));
        var afterDelete = await TaskAsync(client, taskId);

        Assert.Equal(("Versão 2", 2, false), (afterLateEvent.Title, afterLateEvent.Version, afterLateEvent.Deleted));
        Assert.Equal((3, true, v3), (afterDelete.Version, afterDelete.Deleted, afterDelete.LastEventId));
        Assert.Equal(3, (await EventsAsync(client)).UniqueEvents);
    }

    [Fact]
    public async Task A_restart_rebuilds_the_state_and_drops_a_torn_last_line()
    {
        var client = Start();
        var (first, second, taskId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var firstBody = Envelope(first, "task.created", taskId, 1, "Antes do reinício");
        await DeliverAsync(client, first, firstBody);
        await DeliverAsync(client, second, Envelope(second, "task.updated", taskId, 2, "Também antes"));
        await factories[0].DisposeAsync();
        factories.Clear();
        // Queda no meio de uma gravação: última linha incompleta, sem quebra de linha.
        await File.AppendAllTextAsync(DataPath, "{\"kind\":\"event\",\"eventId\":\"");

        var restarted = Start();
        var duplicate = await DeliverAsync(restarted, first, firstBody);
        var third = Guid.NewGuid();
        await DeliverAsync(restarted, third, Envelope(third, "task.deleted", taskId, 3, "Também antes", deleted: true));

        Assert.True(await DuplicateFlagAsync(duplicate));
        var events = await EventsAsync(restarted);
        Assert.Equal((3, 4), (events.UniqueEvents, events.TotalDeliveries));
        Assert.True((await TaskAsync(restarted, taskId)).Deleted);
        Assert.All(LogLines(), line => JsonDocument.Parse(line).Dispose());
    }

    [Fact]
    public void A_failed_write_is_undone_so_the_log_stays_readable()
    {
        Directory.CreateDirectory(directory);
        FailingLogStream? stream = null;
        var store = new EventStore(DataPath, NullLogger<EventStore>.Instance, path => stream = new FailingLogStream(path));
        var (first, failed, third, taskId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        IncomingEvent Incoming(Guid id, int version) =>
            IncomingEvent.TryParse(Encoding.UTF8.GetBytes(Envelope(id, "task.updated", taskId, version)), id.ToString())!;

        store.Accept(Incoming(first, 1), DateTimeOffset.UtcNow, out _);
        stream!.FailNextWrite = true;
        Assert.Throws<IOException>(() => store.Accept(Incoming(failed, 2), DateTimeOffset.UtcNow, out _));
        store.Accept(Incoming(third, 3), DateTimeOffset.UtcNow, out _);
        store.Dispose();

        // Reinício: o log tem só linhas inteiras e o evento que falhou não conta como recebido.
        using var reopened = new EventStore(DataPath, NullLogger<EventStore>.Instance);
        Assert.Equal(new[] { first, third }.Order(), reopened.Events().Events.Select(e => e.EventId).Order());
    }

    [Fact]
    public void A_corrupted_line_in_the_middle_fails_startup()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(DataPath, "isto não é json\n{\"kind\":\"duplicate\"}\n");

        Assert.ThrowsAny<Exception>(() => Start());
    }
}

/// <summary>Janela de ±5 min da assinatura no consumidor, com relógio fixo (bordas exatas, sem depender do tempo real).</summary>
public sealed class ConsumerSignatureWindowTests
{
    private const string Secret = "test-only-webhook-secret-with-32-bytes!!";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static bool Accepts(long timestamp) =>
        WebhookSignature.IsValid(ApiSignature.Create(Secret, timestamp, Body), Body, Secret, Now, toleranceSeconds: 300);

    [Theory]
    [InlineData(0, true)]
    [InlineData(-300, true)]
    [InlineData(300, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    public void Accepts_timestamps_up_to_five_minutes_in_either_direction(long offset, bool accepted) =>
        Assert.Equal(accepted, Accepts(Now.ToUnixTimeSeconds() + offset));

    [Fact]
    public void Extreme_timestamps_are_rejected_without_overflow()
    {
        // "now - t" daria exatamente long.MinValue, e Math.Abs(long.MinValue) lança OverflowException.
        Assert.False(Accepts(unchecked(Now.ToUnixTimeSeconds() + long.MinValue)));
        Assert.False(Accepts(long.MinValue));
        Assert.False(Accepts(long.MaxValue));
    }
}

/// <summary>Log que, quando armado, grava só metade da próxima linha e falha (disco cheio).</summary>
public sealed class FailingLogStream(string path)
    : FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 0)
{
    public bool FailNextWrite { get; set; }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!FailNextWrite)
        {
            base.Write(buffer);
            return;
        }
        FailNextWrite = false;
        base.Write(buffer[..(buffer.Length / 2)]);
        throw new IOException("Disco cheio (simulado).");
    }
}
