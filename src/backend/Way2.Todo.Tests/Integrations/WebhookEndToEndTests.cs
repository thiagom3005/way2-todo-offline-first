using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Way2.Todo.Api.Integrations;
using Way2.Todo.Consumer;
using Way2.Todo.Tests.Support;
using static Way2.Todo.Tests.Support.SyncOperations;

namespace Way2.Todo.Tests.Integrations;

/// <summary>
/// API real (dispatcher em segundo plano ligado) entregando ao consumidor simulado real, em memória:
/// push → outbox no commit → webhook assinado → consumidor deduplica e projeta.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WebhookEndToEndTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Secret = "test-only-webhook-secret-with-32-bytes!!";

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"way2-consumer-{Guid.NewGuid():N}");
    private ConsumerFactory? consumer;

    private ConsumerFactory Consumer => consumer ??= new ConsumerFactory(Path.Combine(directory, "events.jsonl"), Secret);

    private WebApplicationFactory<Program> StartApi() =>
        new ApiFactory(fixture.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Outbox:DispatcherEnabled", "true");
            builder.UseSetting("Outbox:PollIntervalSeconds", "0.2");
            builder.UseSetting("Outbox:BaseBackoffSeconds", "0.2");
            builder.UseSetting("Outbox:MaxBackoffSeconds", "1");
            builder.UseSetting("Outbox:LeaseSeconds", "10");
            builder.UseSetting("Webhook:Url", "http://consumer/webhooks/todo-events");
            builder.UseSetting("Webhook:Secret", Secret);
            builder.UseSetting("Webhook:TimeoutSeconds", "2");
            builder.ConfigureTestServices(services => services
                .AddHttpClient(OutboxDispatcher.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Consumer.Server.CreateHandler()));
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (consumer is not null)
            await consumer.DisposeAsync();
        TempDirectory.TryDelete(directory);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe, TimeSpan timeout) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (await probe() is { } value)
                return value;
            await Task.Delay(50);
        }
        throw new TimeoutException("Condição não atingida a tempo.");
    }

    private Task<StoredEvent> ReceivedAsync(HttpClient consumerClient, Guid taskId, string eventType) =>
        WaitForAsync(async () => (await consumerClient.GetFromJsonAsync<EventsSnapshot>("/events"))!
            .Events.FirstOrDefault(e => e.TaskId == taskId && e.EventType == eventType), TimeSpan.FromSeconds(15));

    private Task<OutboxEvent> DeliveredAsync(Guid userId, long serverRevision) =>
        WaitForAsync(async () => (await fixture.OutboxEventsOfAsync(userId)).FirstOrDefault(e =>
            e.ServerRevision == serverRevision && e.Status == OutboxStatuses.Delivered), TimeSpan.FromSeconds(15));

    [Fact]
    public async Task Changes_reach_the_consumer_quickly_and_failures_are_retried_without_duplicate_effects()
    {
        await fixture.NeutralizePendingEventsAsync();
        using var api = StartApi();
        var (client, userId) = await fixture.NewUserClientAsync(api);
        var consumerClient = Consumer.CreateClient();
        var taskId = Guid.NewGuid();

        // 1) Criação: o sinal pós-commit acorda o dispatcher; entrega quase imediata.
        var watch = Stopwatch.StartNew();
        await PushOneAsync(client, Upsert(taskId, null, "Criada"));
        var created = await ReceivedAsync(consumerClient, taskId, "task.created");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Entrega levou {watch.Elapsed}.");
        Assert.Equal(1, created.Deliveries);

        // 2) Consumidor indisponível em duas tentativas: retry com backoff até entregar.
        await consumerClient.PutAsJsonAsync("/failure-mode", new { statusCode = 503, remaining = 2 });
        await PushOneAsync(client, Upsert(taskId, 1, "Editada"));
        var updated = await ReceivedAsync(consumerClient, taskId, "task.updated");
        var updatedEvent = await DeliveredAsync(userId, 2);
        Assert.Equal((3, 1), (updatedEvent.Attempts, updated.Deliveries));

        // 3) Ack perdido: o consumidor grava e responde 500; o reenvio é reconhecido como duplicado.
        await consumerClient.PutAsJsonAsync("/failure-mode", new { statusCode = 500, remaining = 1, afterProcessing = true });
        await PushOneAsync(client, Delete(taskId, 2));
        var deletedEvent = await DeliveredAsync(userId, 3);
        var deleted = await ReceivedAsync(consumerClient, taskId, "task.deleted");

        Assert.Equal((2, 2), (deletedEvent.Attempts, deleted.Deliveries));
        var snapshot = (await consumerClient.GetFromJsonAsync<EventsSnapshot>("/events"))!;
        Assert.Equal(3, snapshot.Events.Count(e => e.TaskId == taskId));
        var task = (await consumerClient.GetFromJsonAsync<List<TaskView>>("/tasks"))!.Single(t => t.TaskId == taskId);
        Assert.Equal(("Editada", 3, true), (task.Title, task.Version, task.Deleted));
    }

    [Fact]
    public async Task Events_still_pending_when_the_api_stops_are_delivered_after_a_restart()
    {
        await fixture.NeutralizePendingEventsAsync();
        var consumerClient = Consumer.CreateClient();
        await consumerClient.PutAsJsonAsync("/failure-mode", new { statusCode = 503 }); // fora do ar até segunda ordem
        var taskId = Guid.NewGuid();
        Guid userId;
        int attemptsBeforeRestart;

        using (var first = StartApi())
        {
            (var client, userId) = await fixture.NewUserClientAsync(first);
            await PushOneAsync(client, Upsert(taskId, null, "Antes do reinício"));
            var failing = await WaitForAsync(async () =>
                (await fixture.OutboxEventsOfAsync(userId)).FirstOrDefault(e => e.Attempts >= 2), TimeSpan.FromSeconds(15));
            attemptsBeforeRestart = failing.Attempts;
        } // API desligada com o evento pendente

        await consumerClient.DeleteAsync("/failure-mode");
        using var restarted = StartApi();
        _ = restarted.Server; // sobe o host e o dispatcher

        var delivered = await DeliveredAsync(userId, 1);
        var received = await ReceivedAsync(consumerClient, taskId, "task.created");
        Assert.True(delivered.Attempts > attemptsBeforeRestart);
        Assert.Equal(1, received.Deliveries); // os 503 anteriores não foram registrados
    }
}
