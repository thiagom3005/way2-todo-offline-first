using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Integrations;
using Way2.Todo.Api.Sync;
using Way2.Todo.Tests.Support;
using static Way2.Todo.Tests.Support.SyncOperations;

namespace Way2.Todo.Tests.Integrations;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxTests(PostgresFixture fixture)
{
    private async Task<List<OutboxEvent>> EventsOfAsync(Guid userId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.OutboxEvents.AsNoTracking()
            .Where(e => e.UserId == userId).OrderBy(e => e.ServerRevision).ToListAsync();
    }

    [Fact]
    public async Task Create_update_and_delete_each_record_one_event_in_the_same_commit()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        await PushOneAsync(client, Upsert(taskId, null));
        await PushOneAsync(client, Upsert(taskId, 1, "Conferir medição urgente"));
        await PushOneAsync(client, Delete(taskId, 2));

        var events = await EventsOfAsync(userId);

        Assert.Equal(
            [TaskEventTypes.Created, TaskEventTypes.Updated, TaskEventTypes.Deleted],
            events.Select(e => e.EventType));
        Assert.Equal([1, 2, 3], events.Select(e => e.TaskVersion));
        Assert.Equal([1L, 2L, 3L], events.Select(e => e.ServerRevision));
        Assert.All(events, e =>
        {
            Assert.Equal((taskId, OutboxStatuses.Pending, 0, 1), (e.TaskId, e.Status, e.Attempts, e.SchemaVersion));
            Assert.Equal(e.OccurredAt, e.NextRetryAt);
        });

        // O snapshot do evento é exatamente o do change log da mesma revisão, gravado no mesmo instante.
        await using var db = fixture.CreateDbContext();
        var changes = await db.TaskChangeLog.AsNoTracking()
            .Where(c => c.UserId == userId).OrderBy(c => c.Revision).ToListAsync();
        foreach (var (outboxEvent, change) in events.Zip(changes))
        {
            var envelope = JsonNode.Parse(outboxEvent.PayloadJson)!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(change.SnapshotJson), envelope["task"]));
            Assert.Equal(change.CreatedAt, outboxEvent.OccurredAt);
        }
        var deleted = JsonNode.Parse(events[^1].PayloadJson)!;
        Assert.NotNull(deleted["task"]!["deletedAt"]);
    }

    [Fact]
    public async Task Duplicate_conflicting_and_invalid_operations_record_no_event()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var (other, _) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        var create = Upsert(taskId, null, operationId: Guid.NewGuid());
        await PushOneAsync(client, create);

        await PushOneAsync(client, create); // duplicate
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PushOneAsync(client, create))); // duplicates em paralelo
        await PushOneAsync(client, Upsert(taskId, 1, "Aplicada")); // v2
        var conflict = await PushOneAsync(client, Upsert(taskId, 1, "Versão antiga"));
        var invalid = await PushOneAsync(client, Upsert(Guid.NewGuid(), null, priority: "urgent"));
        var reused = await PushOneAsync(client, Upsert(taskId, 2, "Outro conteúdo", (Guid)create["operationId"]!));
        var foreign = await PushOneAsync(other, Upsert(taskId, 2, "Conta alheia"));

        Assert.Equal(SyncStatuses.Conflict, conflict.Status);
        Assert.Equal(SyncStatuses.ValidationError, invalid.Status);
        Assert.Equal(SyncStatuses.ValidationError, reused.Status);
        Assert.Equal(("conflict", "not_found"), (foreign.Status, foreign.Reason));
        var events = await EventsOfAsync(userId);
        Assert.Equal([TaskEventTypes.Created, TaskEventTypes.Updated], events.Select(e => e.EventType));
    }

    [Fact]
    public async Task A_failure_before_commit_rolls_back_the_event_together_with_the_task()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        // Ocupa a revisão 1 do usuário: o INSERT do change log falha DEPOIS da tarefa e do evento.
        await using (var db = fixture.CreateDbContext())
        {
            db.TaskChangeLog.Add(new TaskChangeLogEntry
            {
                UserId = userId,
                Revision = 1,
                TaskId = Guid.NewGuid(),
                ChangeType = "upsert",
                SnapshotJson = "{}",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var taskId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            "/api/sync/push", new { deviceId = DeviceId, operations = new[] { Upsert(taskId, null) } });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var check = fixture.CreateDbContext();
        Assert.False(await check.Tasks.AnyAsync(t => t.Id == taskId));
        Assert.False(await check.ProcessedOperations.AnyAsync(p => p.UserId == userId));
        Assert.Empty(await EventsOfAsync(userId));
    }

    [Fact]
    public async Task Parallel_pushes_record_one_event_per_revision()
    {
        var (client, userId) = await fixture.NewUserClientAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            PushOneAsync(client, Upsert(Guid.NewGuid(), null, $"Tarefa {i}"))));

        var events = await EventsOfAsync(userId);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), events.Select(e => e.ServerRevision));
        Assert.All(events, e => Assert.Equal(TaskEventTypes.Created, e.EventType));
    }

    [Fact]
    public async Task Event_payload_follows_the_contract()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        await PushOneAsync(client, Upsert(taskId, null));

        var outboxEvent = Assert.Single(await EventsOfAsync(userId));
        using var json = JsonDocument.Parse(outboxEvent.PayloadJson);
        var root = json.RootElement;

        string[] envelope =
            ["eventId", "eventType", "occurredAt", "schemaVersion", "serverRevision", "task", "taskId", "taskVersion", "userId"];
        Assert.Equal(envelope, root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(outboxEvent.EventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal("task.created", root.GetProperty("eventType").GetString());
        Assert.Equal(userId, root.GetProperty("userId").GetGuid());
        Assert.Equal(taskId, root.GetProperty("taskId").GetGuid());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.EndsWith("Z", root.GetProperty("occurredAt").GetString());
        Assert.Equal("2026-10-30", root.GetProperty("task").GetProperty("dueDate").GetString());
        Assert.Equal("medium", root.GetProperty("task").GetProperty("priority").GetString());
    }

    [Fact]
    public async Task Outbox_monitoring_shows_only_the_callers_events()
    {
        var (alice, _) = await fixture.NewUserClientAsync();
        var (bob, _) = await fixture.NewUserClientAsync();
        var aliceTasks = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var bobTask = Guid.NewGuid();
        foreach (var id in aliceTasks) await PushOneAsync(alice, Upsert(id, null));
        await PushOneAsync(bob, Upsert(bobTask, null));

        var summary = (await alice.GetFromJsonAsync<OutboxSummary>("/api/integrations/outbox", SyncJson.Options))!;
        var anonymous = await fixture.Api.CreateClient().GetAsync("/api/integrations/outbox");

        Assert.Equal((2, 0, 0, 0), (summary.Pending, summary.Retrying, summary.Delivered, summary.BlockedTasks));
        Assert.Equal(aliceTasks.Order(), summary.Recent.Select(e => e.TaskId).Order());
        Assert.DoesNotContain(bobTask, summary.Recent.Select(e => e.TaskId));
        Assert.NotNull(summary.OldestPendingAgeSeconds);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
