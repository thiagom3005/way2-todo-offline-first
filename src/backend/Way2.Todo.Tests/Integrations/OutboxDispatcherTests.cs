using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Way2.Todo.Api.Integrations;
using Way2.Todo.Api.Tasks;
using Way2.Todo.Tests.Support;
using static Way2.Todo.Tests.Support.SyncOperations;

namespace Way2.Todo.Tests.Integrations;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxDispatcherTests(PostgresFixture fixture)
{
    private const string Secret = "test-only-webhook-secret-with-32-bytes!!";

    private static readonly WebhookOptions Webhook = new()
    {
        Url = "http://consumer.test/webhooks/todo-events",
        Secret = Secret,
        TimeoutSeconds = 1,
    };

    private static readonly OutboxOptions Outbox = new()
    {
        BatchSize = 20,
        LeaseSeconds = 30,
        BaseBackoffSeconds = 2,
        MaxBackoffSeconds = 300,
        PollIntervalSeconds = 0.1,
    };

    // Truncado em ms: o PostgreSQL guarda microssegundos, então comparações exatas funcionam.
    private readonly FixedTimeProvider clock = new(UtcTimestamp.Truncate(DateTime.UtcNow.AddSeconds(1)));
    private readonly ScriptedWebhook webhook = new();

    private OutboxDispatcher Dispatcher(ScriptedWebhook? handler = null, IServiceScopeFactory? scopes = null) => new(
        scopes ?? fixture.Api.Services.GetRequiredService<IServiceScopeFactory>(),
        new SingleClientFactory(handler ?? webhook, TimeSpan.FromSeconds(Webhook.TimeoutSeconds)),
        new OutboxSignal(),
        Options.Create(Outbox),
        Options.Create(Webhook),
        clock,
        NullLogger<OutboxDispatcher>.Instance,
        jitter: () => 0.5);

    /// <summary>Banco compartilhado: neutraliza pendências de outros testes antes de cada cenário.</summary>
    private async Task<(HttpClient Client, Guid UserId)> IsolatedUserAsync()
    {
        await fixture.NeutralizePendingEventsAsync();
        return await fixture.NewUserClientAsync();
    }

    private Task<List<OutboxEvent>> EventsOfAsync(Guid userId) => fixture.OutboxEventsOfAsync(userId);

    /// <summary>
    /// Eventos recém-criados pelo push, com o relógio do dispatcher posto logo depois deles: o push usa o
    /// relógio real, e a API pode ter levado segundos para subir quando o teste roda sozinho.
    /// </summary>
    private async Task<List<OutboxEvent>> NewEventsAsync(Guid userId)
    {
        var events = await EventsOfAsync(userId);
        var latest = events.Max(e => e.NextRetryAt).AddMilliseconds(1);
        if (latest > clock.UtcNow)
            clock.UtcNow = latest;
        return events;
    }

    private async Task<OutboxEvent> EventAsync(Guid eventId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.OutboxEvents.AsNoTracking().SingleAsync(e => e.EventId == eventId);
    }

    /// <summary>Simula outro ciclo segurando a reserva do evento (ou um processo que caiu no meio do envio).</summary>
    private async Task HoldLeaseAsync(Guid eventId, DateTime until)
    {
        await using var db = fixture.CreateDbContext();
        await db.OutboxEvents.Where(e => e.EventId == eventId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.LockedUntil, until)
            .SetProperty(e => e.ClaimToken, Guid.NewGuid()));
    }

    private Guid[] Sent() => [.. webhook.Requests.Select(r => r.EventId)];

    private static long TimestampOf(ReceivedWebhook request) =>
        long.Parse(request.Signature.Split(',')[0]["t=".Length..]);

    private void AssertValidSignature(ReceivedWebhook request)
    {
        var timestamp = TimestampOf(request);
        Assert.Equal(new DateTimeOffset(clock.UtcNow).ToUnixTimeSeconds(), timestamp);
        Assert.Equal(WebhookSignature.Create(Secret, timestamp, Encoding.UTF8.GetBytes(request.Body)), request.Signature);
    }

    [Fact]
    public async Task Delivers_signed_events_and_marks_them_delivered()
    {
        var (client, userId) = await IsolatedUserAsync();
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null));
        var outboxEvent = Assert.Single(await NewEventsAsync(userId));

        Assert.Equal(1, await Dispatcher().DispatchOnceAsync());

        var request = Assert.Single(webhook.Requests);
        Assert.Equal((outboxEvent.EventId, TaskEventTypes.Created), (request.EventId, request.EventType));
        Assert.Equal(outboxEvent.PayloadJson, request.Body);
        Assert.StartsWith("application/json", request.ContentType);
        AssertValidSignature(request);
        var delivered = await EventAsync(outboxEvent.EventId);
        Assert.Equal((OutboxStatuses.Delivered, 1, (int?)200, (string?)null), (delivered.Status, delivered.Attempts, delivered.LastStatusCode, delivered.LastError));
        Assert.Equal(clock.UtcNow, delivered.DeliveredAt);
        Assert.Null(delivered.LockedUntil);
        Assert.Null(delivered.ClaimToken);
    }

    [Fact]
    public async Task Http_500_is_retried_after_backoff_with_a_fresh_timestamp_and_signature()
    {
        var (client, userId) = await IsolatedUserAsync();
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null));
        var id = Assert.Single(await NewEventsAsync(userId)).EventId;
        webhook.Respond = (_, attempt, _) =>
            Task.FromResult(attempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        var dispatcher = Dispatcher();

        await dispatcher.DispatchOnceAsync();
        var failed = await EventAsync(id);
        Assert.Equal((OutboxStatuses.Pending, 1, (int?)500, "HTTP 500"), (failed.Status, failed.Attempts, failed.LastStatusCode, failed.LastError));
        Assert.Equal(clock.UtcNow.AddSeconds(2), failed.NextRetryAt);
        Assert.Equal(0, await dispatcher.DispatchOnceAsync()); // ainda em backoff

        clock.UtcNow = clock.UtcNow.AddHours(1); // indisponibilidade longa
        Assert.Equal(1, await dispatcher.DispatchOnceAsync());

        var deliveries = webhook.Requests.ToArray();
        Assert.Equal([id, id], deliveries.Select(d => d.EventId));
        Assert.Equal(deliveries[0].Body, deliveries[1].Body);
        Assert.True(TimestampOf(deliveries[1]) - TimestampOf(deliveries[0]) >= 3600);
        Assert.NotEqual(deliveries[0].Signature, deliveries[1].Signature);
        AssertValidSignature(deliveries[1]);
        var delivered = await EventAsync(id);
        Assert.Equal((OutboxStatuses.Delivered, 2), (delivered.Status, delivered.Attempts));
    }

    [Fact]
    public async Task Timeout_is_recorded_and_releases_the_rest_of_the_batch_without_spending_attempts()
    {
        var (client, userId) = await IsolatedUserAsync();
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null, "A"));
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null, "B"));
        var events = await NewEventsAsync(userId);
        var (a, b) = (events[0].EventId, events[1].EventId);
        webhook.Respond = async (request, attempt, ct) =>
        {
            if (request.EventId == a && attempt == 1)
                await Task.Delay(Timeout.Infinite, ct); // consumidor pendurado
            return HttpStatusCode.OK;
        };
        var dispatcher = Dispatcher();

        Assert.Equal(2, await dispatcher.DispatchOnceAsync());

        var timedOut = await EventAsync(a);
        Assert.Equal((OutboxStatuses.Pending, 1, (int?)null, "Timeout"), (timedOut.Status, timedOut.Attempts, timedOut.LastStatusCode, timedOut.LastError));
        var released = await EventAsync(b);
        Assert.Equal((OutboxStatuses.Pending, 0), (released.Status, released.Attempts));
        Assert.Null(released.ClaimToken);
        Assert.Equal([a], Sent());

        Assert.Equal(1, await dispatcher.DispatchOnceAsync()); // B segue; A está em backoff
        Assert.Equal(OutboxStatuses.Delivered, (await EventAsync(b)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Non_2xx_responses_are_never_marked_as_delivered(HttpStatusCode status)
    {
        var (client, userId) = await IsolatedUserAsync();
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null));
        var id = Assert.Single(await NewEventsAsync(userId)).EventId;
        webhook.Respond = (_, _, _) => Task.FromResult(status);

        await Dispatcher().DispatchOnceAsync();

        var outboxEvent = await EventAsync(id);
        Assert.Equal((OutboxStatuses.Pending, 1, (int?)status), (outboxEvent.Status, outboxEvent.Attempts, outboxEvent.LastStatusCode));
    }

    [Fact]
    public async Task Later_events_of_a_task_wait_while_any_earlier_event_is_pending()
    {
        var (client, userId) = await IsolatedUserAsync();
        var (task, other) = (Guid.NewGuid(), Guid.NewGuid());
        await PushOneAsync(client, Upsert(task, null, "v1"));
        await PushOneAsync(client, Upsert(task, 1, "v2"));
        await PushOneAsync(client, Upsert(task, 2, "v3"));
        await PushOneAsync(client, Upsert(other, null, "Outra tarefa"));
        var events = await NewEventsAsync(userId);
        var (t1, t2, t3, u1) = (events[0].EventId, events[1].EventId, events[2].EventId, events[3].EventId);
        webhook.Respond = (request, attempt, _) => Task.FromResult(
            request.EventId == t1 && attempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        var dispatcher = Dispatcher();

        // Só as cabeças saem: t1 (falha e entra em backoff) e a outra tarefa, que não é bloqueada.
        Assert.Equal(2, await dispatcher.DispatchOnceAsync());
        Assert.Equal([t1, u1], Sent());

        // Em backoff: t2 e t3 já venceram, mas t1 continua pendente.
        Assert.Equal(0, await dispatcher.DispatchOnceAsync());

        // Retry vencido, mas t1 reservado por outro ciclo: nada da tarefa sai.
        clock.UtcNow = clock.UtcNow.AddSeconds(3);
        await HoldLeaseAsync(t1, until: clock.UtcNow.AddSeconds(30));
        Assert.Equal(0, await dispatcher.DispatchOnceAsync());

        // Reserva expirada: estritamente t1 → t2 → t3, um por ciclo.
        clock.UtcNow = clock.UtcNow.AddSeconds(31);
        for (var i = 0; i < 3; i++)
            Assert.Equal(1, await dispatcher.DispatchOnceAsync());
        Assert.Equal([t1, u1, t1, t2, t3], Sent());
        Assert.Equal(0, await dispatcher.DispatchOnceAsync());
    }

    [Fact]
    public async Task Concurrent_dispatchers_deliver_each_event_once_and_in_task_order()
    {
        var (client, userId) = await IsolatedUserAsync();
        var task = Guid.NewGuid();
        await PushOneAsync(client, Upsert(task, null, "v1"));
        for (var version = 1; version <= 4; version++)
            await PushOneAsync(client, Upsert(task, version, $"v{version + 1}"));
        for (var i = 0; i < 5; i++)
            await PushOneAsync(client, Upsert(Guid.NewGuid(), null, $"Outra {i}"));
        var events = await NewEventsAsync(userId);
        var taskEvents = events.Where(e => e.TaskId == task).Select(e => e.EventId).ToArray();
        webhook.Respond = async (_, _, ct) =>
        {
            await Task.Delay(Random.Shared.Next(5, 25), ct);
            return HttpStatusCode.OK;
        };

        async Task DrainAsync(OutboxDispatcher dispatcher)
        {
            for (var i = 0; i < 200 && (await EventsOfAsync(userId)).Any(e => e.Status == OutboxStatuses.Pending); i++)
            {
                if (await dispatcher.DispatchOnceAsync() == 0)
                    await Task.Delay(5);
            }
        }
        await Task.WhenAll(DrainAsync(Dispatcher()), DrainAsync(Dispatcher()));

        var sent = Sent();
        Assert.Equal(events.Count, sent.Length);
        Assert.Equal(events.Count, sent.Distinct().Count());
        Assert.Equal(taskEvents, sent.Where(taskEvents.Contains));
    }

    [Fact]
    public async Task A_result_from_an_expired_claim_is_ignored()
    {
        var (client, userId) = await IsolatedUserAsync();
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null));
        var id = Assert.Single(await NewEventsAsync(userId)).EventId;
        var second = new ScriptedWebhook();
        webhook.Respond = async (_, _, ct) =>
        {
            // Enquanto o primeiro ciclo ainda espera a resposta, a reserva expira e outro ciclo entrega.
            clock.UtcNow = clock.UtcNow.AddSeconds(Outbox.LeaseSeconds + 1);
            Assert.Equal(1, await Dispatcher(second).DispatchOnceAsync(ct));
            return HttpStatusCode.InternalServerError;
        };

        await Dispatcher().DispatchOnceAsync();

        var outboxEvent = await EventAsync(id);
        Assert.Equal((OutboxStatuses.Delivered, 1, (int?)200), (outboxEvent.Status, outboxEvent.Attempts, outboxEvent.LastStatusCode));
        Assert.Single(second.Requests);
    }

    [Fact]
    public async Task Pending_events_are_delivered_after_a_restart()
    {
        var (client, userId) = await IsolatedUserAsync();
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null));
        var id = Assert.Single(await NewEventsAsync(userId)).EventId;
        webhook.Respond = (_, _, _) => Task.FromResult(HttpStatusCode.ServiceUnavailable);
        await Dispatcher().DispatchOnceAsync();
        // O processo cai no meio da próxima entrega, segurando a reserva.
        clock.UtcNow = clock.UtcNow.AddSeconds(3);
        await HoldLeaseAsync(id, until: clock.UtcNow.AddSeconds(30));

        // "Reinício": nova instância, consumidor de volta. Antes de a reserva expirar, nada sai.
        var restarted = new ScriptedWebhook();
        Assert.Equal(0, await Dispatcher(restarted).DispatchOnceAsync());
        clock.UtcNow = clock.UtcNow.AddSeconds(31);
        Assert.Equal(1, await Dispatcher(restarted).DispatchOnceAsync());

        var outboxEvent = await EventAsync(id);
        Assert.Equal((OutboxStatuses.Delivered, 2), (outboxEvent.Status, outboxEvent.Attempts));
        Assert.Equal([id], restarted.Requests.Select(r => r.EventId));
    }

    [Fact]
    public async Task A_failing_cycle_does_not_stop_the_background_dispatcher()
    {
        var (client, userId) = await IsolatedUserAsync();
        await PushOneAsync(client, Upsert(Guid.NewGuid(), null));
        var id = Assert.Single(await NewEventsAsync(userId)).EventId;
        var flaky = new FlakyScopeFactory(fixture.Api.Services.GetRequiredService<IServiceScopeFactory>(), failures: 2);
        using var dispatcher = Dispatcher(scopes: flaky);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 100 && webhook.Requests.IsEmpty; i++)
                await Task.Delay(50);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, flaky.FailuresThrown);
        Assert.Equal([id], Sent());
        Assert.Equal(OutboxStatuses.Delivered, (await EventAsync(id)).Status);
    }
}

public sealed class OutboxBackoffTests
{
    private static readonly OutboxOptions Options = new() { BaseBackoffSeconds = 2, MaxBackoffSeconds = 300 };

    [Fact]
    public void Backoff_doubles_up_to_the_cap_and_never_overflows()
    {
        Assert.Equal(
            [2, 4, 8, 16, 32, 64, 128, 256, 300, 300],
            Enumerable.Range(1, 10).Select(n => OutboxDispatcher.BackoffDelay(n, Options, 0.5).TotalSeconds));

        foreach (var attempts in Enumerable.Range(1, 200))
        {
            foreach (var jitter in new[] { 0.0, 0.5, 0.999 })
            {
                var delay = OutboxDispatcher.BackoffDelay(attempts, Options, jitter);
                Assert.InRange(delay.TotalSeconds, 1.6, 300);
            }
        }
        Assert.Equal(1.6, OutboxDispatcher.BackoffDelay(1, Options, 0).TotalSeconds, 3);
        Assert.Equal(2.4, OutboxDispatcher.BackoffDelay(1, Options, 1).TotalSeconds, 3);
    }
}

public sealed class OutboxSignalTests
{
    [Fact]
    public async Task Notifying_a_saturated_signal_never_throws()
    {
        var signal = new OutboxSignal();
        for (var i = 0; i < 1000; i++)
            signal.Notify();

        // Um aviso pendente libera a primeira espera na hora; os outros 999 foram descartados sem erro.
        await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        await signal.WaitAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(90));
    }
}

public sealed record ReceivedWebhook(Guid EventId, string EventType, string Signature, string Body, string? ContentType);

/// <summary>Consumidor falso: grava cada requisição e responde conforme o roteiro (por evento e tentativa).</summary>
public sealed class ScriptedWebhook : HttpMessageHandler
{
    private readonly ConcurrentDictionary<Guid, int> attempts = new();

    public ConcurrentQueue<ReceivedWebhook> Requests { get; } = new();

    public Func<ReceivedWebhook, int, CancellationToken, Task<HttpStatusCode>>? Respond { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var received = new ReceivedWebhook(
            Guid.Parse(request.Headers.GetValues(OutboxDispatcher.EventIdHeader).Single()),
            request.Headers.GetValues(OutboxDispatcher.EventTypeHeader).Single(),
            request.Headers.GetValues(WebhookSignature.Header).Single(),
            await request.Content!.ReadAsStringAsync(ct),
            request.Content.Headers.ContentType?.ToString());
        Requests.Enqueue(received);
        var attempt = attempts.AddOrUpdate(received.EventId, 1, (_, n) => n + 1);
        var status = Respond is null ? HttpStatusCode.OK : await Respond(received, attempt, ct);
        return new HttpResponseMessage(status);
    }
}

public sealed class SingleClientFactory(HttpMessageHandler handler, TimeSpan timeout) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout };
}

/// <summary>Falha as primeiras N aberturas de escopo (banco indisponível) e depois delega.</summary>
public sealed class FlakyScopeFactory(IServiceScopeFactory inner, int failures) : IServiceScopeFactory
{
    private int calls;

    public int FailuresThrown => Math.Min(Volatile.Read(ref calls), failures);

    public IServiceScope CreateScope() =>
        Interlocked.Increment(ref calls) <= failures
            ? throw new InvalidOperationException("Banco indisponível (simulado).")
            : inner.CreateScope();
}
