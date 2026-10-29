using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Sync;
using Way2.Todo.Api.Tasks;
using Way2.Todo.Tests.Support;
using static Way2.Todo.Tests.Support.SyncOperations;

namespace Way2.Todo.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SyncApiTests(PostgresFixture fixture)
{
    /// <summary>Confere no banco quantas operações foram processadas e quais revisões existem no change log.</summary>
    private async Task AssertServerStateAsync(Guid userId, int processed, params long[] revisions)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(processed, await db.ProcessedOperations.CountAsync(p => p.UserId == userId));
        Assert.Equal(revisions, await db.TaskChangeLog.Where(c => c.UserId == userId)
            .OrderBy(c => c.Revision).Select(c => c.Revision).ToArrayAsync());
    }

    private static void AssertPage(PullResponse page, long[] revisions, long nextCursor, bool hasMore)
    {
        Assert.Equal(revisions, page.Changes.Select(c => c.Revision));
        Assert.Equal(nextCursor, page.NextCursor);
        Assert.Equal(hasMore, page.HasMore);
    }

    [Fact]
    public async Task A_push_body_over_the_size_limit_is_rejected_before_any_processing()
    {
        // O limite é aplicado pelo servidor HTTP (IHttpMaxRequestBodySizeFeature), que o TestServer não tem: Kestrel real.
        await using var api = new ApiFactory(fixture.ConnectionString);
        api.UseKestrel(0);
        api.StartServer();
        var (client, userId) = await fixture.NewUserClientAsync(api);
        var padding = new string('a', 2 * SyncLimits.MaxPushBodyBytes);
        using var body = new StringContent(
            $$"""{"deviceId":"{{DeviceId}}","operations":[],"padding":"{{padding}}"}""", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/sync/push", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertServerStateAsync(userId, processed: 0);
    }

    [Fact]
    public async Task Create_update_and_delete_are_applied_with_contiguous_revisions()
    {
        var (client, _) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();

        var created = await PushOneAsync(client, Upsert(taskId, null));
        var updated = await PushOneAsync(client, Upsert(taskId, 1, "Conferir medição urgente"));
        var deleted = await PushOneAsync(client, Delete(taskId, 2));

        Assert.Equal((SyncStatuses.Applied, 1L, 1), (created.Status, created.ServerRevision!.Value, created.ServerTask!.Version));
        Assert.Equal((SyncStatuses.Applied, 2L, 2), (updated.Status, updated.ServerRevision!.Value, updated.ServerTask!.Version));
        Assert.Equal("Conferir medição urgente", updated.ServerTask.Title);
        Assert.Equal((SyncStatuses.Applied, 3L, 3), (deleted.Status, deleted.ServerRevision!.Value, deleted.ServerTask!.Version));
        Assert.NotNull(deleted.ServerTask.DeletedAt);

        var pull = await PullAsync(client, 0);
        Assert.Equal([1L, 2L, 3L], pull.Changes.Select(c => c.Revision));
        Assert.Equal(["upsert", "upsert", "delete"], pull.Changes.Select(c => c.Action));
        Assert.NotNull(pull.Changes[^1].Task.DeletedAt);
        Assert.Equal((3L, false), (pull.NextCursor, pull.HasMore));
    }

    [Fact]
    public async Task Retrying_an_operation_returns_the_original_result_without_reapplying()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        var create = Upsert(taskId, null);

        var first = await PushOneAsync(client, create);
        var retry = await PushOneAsync(client, create);
        var update = Upsert(taskId, 1, "Segunda versão");
        var sameBatch = await PushAsync(client, update, update);

        Assert.Equal(SyncStatuses.Applied, first.Status);
        Assert.Equal(SyncStatuses.Duplicate, retry.Status);
        Assert.Equal(first.ServerRevision, retry.ServerRevision);
        Assert.Equal(first.ServerTask, retry.ServerTask);
        Assert.Equal([SyncStatuses.Applied, SyncStatuses.Duplicate], sameBatch.Select(r => r.Status));
        await AssertServerStateAsync(userId, 2, 1, 2);
    }

    [Fact]
    public async Task Reusing_an_operation_id_with_different_content_is_rejected()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var original = await PushOneAsync(client, Upsert(taskId, null, "Original", operationId));

        var otherTitle = await PushOneAsync(client, Upsert(taskId, null, "Outro conteúdo", operationId));
        var otherAction = await PushOneAsync(client, Delete(taskId, 1, operationId));
        var otherBase = await PushOneAsync(client, Upsert(taskId, 1, "Original", operationId));
        var otherTask = await PushOneAsync(client, Upsert(Guid.NewGuid(), null, "Original", operationId));
        // Mesmo conteúdo após normalização (trim): continua sendo um retry legítimo.
        var sameNormalized = await PushOneAsync(client, Upsert(taskId, null, "  Original  ", operationId));

        Assert.Equal(SyncStatuses.Applied, original.Status);
        foreach (var rejected in new[] { otherTitle, otherAction, otherBase, otherTask })
        {
            Assert.Equal(SyncStatuses.ValidationError, rejected.Status);
            Assert.Contains("operationId", rejected.Errors!.Keys);
            Assert.Null(rejected.ServerTask);
        }
        Assert.Equal(SyncStatuses.Duplicate, sameNormalized.Status);

        var pull = await PullAsync(client, 0);
        var task = Assert.Single(pull.Changes).Task;
        Assert.Equal(("Original", 1, (DateTime?)null), (task.Title, task.Version, task.DeletedAt));
        await AssertServerStateAsync(userId, 1, 1);
    }

    [Fact]
    public async Task Concurrent_retries_of_the_same_operation_apply_once()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var create = Upsert(Guid.NewGuid(), null);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PushOneAsync(client, create)));

        Assert.Single(results, r => r.Status == SyncStatuses.Applied);
        Assert.Equal(7, results.Count(r => r.Status == SyncStatuses.Duplicate));
        Assert.All(results, r => Assert.Equal(1L, r.ServerRevision));
        await AssertServerStateAsync(userId, 1, 1);
    }

    [Fact]
    public async Task Two_devices_updating_the_same_version_conflict_without_overwriting()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        await PushOneAsync(client, Upsert(taskId, null));

        var deviceA = await PushOneAsync(client, Upsert(taskId, 1, "Versão do dispositivo A"));
        var deviceBOperation = Upsert(taskId, 1, "Versão do dispositivo B");
        var deviceB = await PushOneAsync(client, deviceBOperation);

        Assert.Equal(SyncStatuses.Applied, deviceA.Status);
        Assert.Equal((SyncStatuses.Conflict, "version_mismatch"), (deviceB.Status, deviceB.Reason));
        Assert.Equal(("Versão do dispositivo A", 2), (deviceB.ServerTask!.Title, deviceB.ServerTask.Version));

        // O retry do conflito é reavaliado: continua conflito, nada é gravado nem consome revisão.
        var retry = await PushOneAsync(client, deviceBOperation);
        Assert.Equal((SyncStatuses.Conflict, "version_mismatch"), (retry.Status, retry.Reason));
        await AssertServerStateAsync(userId, 2, 1, 2);

        // Em paralelo, a partir da mesma versão: exatamente um vence.
        var racing = await Task.WhenAll(
            PushOneAsync(client, Upsert(taskId, 2, "Corrida 1")),
            PushOneAsync(client, Upsert(taskId, 2, "Corrida 2")));
        Assert.Single(racing, r => r.Status == SyncStatuses.Applied);
        Assert.Single(racing, r => r.Status == SyncStatuses.Conflict && r.Reason == "version_mismatch");
    }

    [Fact]
    public async Task Operations_on_a_tombstone_return_deleted()
    {
        var (client, _) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        await PushOneAsync(client, Upsert(taskId, null));
        await PushOneAsync(client, Delete(taskId, 1));

        var update = await PushOneAsync(client, Upsert(taskId, 2, "Tentando ressuscitar"));
        var deleteAgain = await PushOneAsync(client, Delete(taskId, 2));

        foreach (var result in new[] { update, deleteAgain })
        {
            Assert.Equal((SyncStatuses.Conflict, "deleted"), (result.Status, result.Reason));
            Assert.NotNull(result.ServerTask!.DeletedAt);
        }
    }

    [Fact]
    public async Task Delete_without_base_version_is_rejected_and_never_creates()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();

        var result = await PushOneAsync(client, Delete(taskId, null));

        Assert.Equal(SyncStatuses.ValidationError, result.Status);
        Assert.Contains("baseVersion", result.Errors!.Keys);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Tasks.AnyAsync(t => t.Id == taskId));
        await AssertServerStateAsync(userId, 0);
    }

    [Fact]
    public async Task An_invalid_operation_does_not_block_the_rest_of_the_batch()
    {
        var (client, _) = await fixture.NewUserClientAsync();

        var results = await PushAsync(client,
            Upsert(Guid.NewGuid(), null, priority: "urgent"),
            Upsert(Guid.NewGuid(), null, dueDate: "30/10/2026"),
            Upsert(Guid.NewGuid(), null, title: "   "),
            Upsert(Guid.NewGuid(), null, "Válida"));

        Assert.Equal(
            [SyncStatuses.ValidationError, SyncStatuses.ValidationError, SyncStatuses.ValidationError, SyncStatuses.Applied],
            results.Select(r => r.Status));
        Assert.Contains("priority", results[0].Errors!.Keys);
        Assert.Contains("dueDate", results[1].Errors!.Keys);
        Assert.Contains("title", results[2].Errors!.Keys);
        Assert.Equal(1L, results[3].ServerRevision);
    }

    [Fact]
    public async Task Text_the_database_cannot_store_is_rejected_only_in_its_operation()
    {
        var (client, userId) = await fixture.NewUserClientAsync();
        var (first, nul, third) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var results = await PushAsync(client,
            Upsert(first, null, "Antes"),
            Upsert(nul, null, "Com\u0000nulo"),
            Upsert(third, null, "Depois"));

        Assert.Equal(
            [SyncStatuses.Applied, SyncStatuses.ValidationError, SyncStatuses.Applied],
            results.Select(r => r.Status));
        Assert.Contains("title", results[1].Errors!.Keys);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Tasks.AnyAsync(t => t.Id == nul));
        Assert.Equal(2, await db.OutboxEvents.CountAsync(e => e.UserId == userId));
        await AssertServerStateAsync(userId, 2, 1, 2);
    }

    [Fact]
    public async Task A_database_data_error_rolls_back_only_its_operation()
    {
        // Gatilho de teste: o PostgreSQL recusa este título com SQLSTATE 22023 (erro de dados),
        // depois que a validação da aplicação já aprovou a operação.
        const string forced = "forçar-sqlstate-22";
        await using (var setup = fixture.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION test_force_data_exception() RETURNS trigger AS $$
                BEGIN
                  IF NEW.title = '{forced}' THEN
                    RAISE EXCEPTION 'falha de dados simulada' USING ERRCODE = '22023';
                  END IF;
                  RETURN NEW;
                END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_force_data_exception BEFORE INSERT OR UPDATE ON tasks
                  FOR EACH ROW EXECUTE FUNCTION test_force_data_exception();
                """);
        }
        try
        {
            var (client, userId) = await fixture.NewUserClientAsync();
            var (task, other) = (Guid.NewGuid(), Guid.NewGuid());
            await PushOneAsync(client, Upsert(task, null, "Original"));

            // [inválida (update), válida (create), válida (update na MESMA base da recusada)]
            var results = await PushAsync(client,
                Upsert(task, 1, forced),
                Upsert(other, null, "Outra"),
                Upsert(task, 1, "Editada"));

            Assert.Equal(
                [SyncStatuses.ValidationError, SyncStatuses.Applied, SyncStatuses.Applied],
                results.Select(r => r.Status));
            Assert.Contains("payload", results[0].Errors!.Keys);
            // A recusada não deixou rastro: a versão não avançou (a 3ª aplicou sobre a base 1),
            // e não houve revisão, operação processada nem evento para ela.
            Assert.Equal(("Editada", 2), (results[2].ServerTask!.Title, results[2].ServerTask!.Version));
            await AssertServerStateAsync(userId, 3, 1, 2, 3);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(3, await db.OutboxEvents.CountAsync(e => e.UserId == userId));
        }
        finally
        {
            await using var cleanup = fixture.CreateDbContext();
            await cleanup.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS test_force_data_exception ON tasks;
                DROP FUNCTION IF EXISTS test_force_data_exception();
                """);
        }
    }

    [Fact]
    public async Task A_null_operation_is_rejected_without_failing_the_batch()
    {
        var (client, _) = await fixture.NewUserClientAsync();

        var response = await client.PostAsync("/api/sync/push", new StringContent(
            JsonSerializer.Serialize(new { deviceId = DeviceId, operations = new object?[] { null, Upsert(Guid.NewGuid(), null) } }),
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<PushResponse>(SyncJson.Options))!.Results;
        Assert.Equal([SyncStatuses.ValidationError, SyncStatuses.Applied], results.Select(r => r.Status));
    }

    [Fact]
    public async Task Users_cannot_read_or_change_each_other_tasks()
    {
        var (alice, aliceId) = await fixture.NewUserClientAsync();
        var (bob, _) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();
        await PushOneAsync(alice, Upsert(taskId, null, "Tarefa da Alice"));

        var update = await PushOneAsync(bob, Upsert(taskId, 1, "Bob alterou"));
        var delete = await PushOneAsync(bob, Delete(taskId, 1));
        var create = await PushOneAsync(bob, Upsert(taskId, null, "Bob criou"));

        Assert.Equal((SyncStatuses.Conflict, "not_found", (TaskSnapshot?)null), (update.Status, update.Reason, update.ServerTask));
        Assert.Equal((SyncStatuses.Conflict, "not_found", (TaskSnapshot?)null), (delete.Status, delete.Reason, delete.ServerTask));
        Assert.Equal((SyncStatuses.Conflict, "already_exists", (TaskSnapshot?)null), (create.Status, create.Reason, create.ServerTask));
        Assert.Empty((await PullAsync(bob, 0)).Changes);
        var aliceTask = Assert.Single((await PullAsync(alice, 0)).Changes).Task;
        Assert.Equal(("Tarefa da Alice", 1), (aliceTask.Title, aliceTask.Version));
        await AssertServerStateAsync(aliceId, 1, 1);
    }

    [Fact]
    public async Task Concurrent_creates_of_the_same_task_id_by_two_users_keep_one_owner()
    {
        var (alice, _) = await fixture.NewUserClientAsync();
        var (bob, _) = await fixture.NewUserClientAsync();

        for (var i = 0; i < 5; i++)
        {
            var taskId = Guid.NewGuid();
            var results = await Task.WhenAll(
                PushOneAsync(alice, Upsert(taskId, null, "Alice")),
                PushOneAsync(bob, Upsert(taskId, null, "Bob")));

            Assert.Single(results, r => r.Status == SyncStatuses.Applied);
            var loser = Assert.Single(results, r => r.Status == SyncStatuses.Conflict);
            Assert.Equal(("already_exists", (TaskSnapshot?)null), (loser.Reason, loser.ServerTask));
        }
    }

    [Fact]
    public async Task Parallel_pushes_produce_unique_contiguous_revisions()
    {
        var (client, userId) = await fixture.NewUserClientAsync();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => PushOneAsync(client, Upsert(Guid.NewGuid(), null, $"Tarefa {i}"))));

        Assert.All(results, r => Assert.Equal(SyncStatuses.Applied, r.Status));
        var expected = Enumerable.Range(1, 20).Select(i => (long)i).ToArray();
        Assert.Equal(expected, results.Select(r => r.ServerRevision!.Value).Order());
        await AssertServerStateAsync(userId, 20, expected);
    }

    [Fact]
    public async Task Pull_pages_through_all_changes_without_loss_or_repetition()
    {
        var (client, _) = await fixture.NewUserClientAsync();
        await PushAsync(client, [.. Enumerable.Range(0, 7).Select(i => (object)Upsert(Guid.NewGuid(), null, $"Tarefa {i}"))]);

        var page1 = await PullAsync(client, 0, limit: 3);
        var page2 = await PullAsync(client, page1.NextCursor, limit: 3);
        var page3 = await PullAsync(client, page2.NextCursor, limit: 3);
        var empty = await PullAsync(client, page3.NextCursor, limit: 3);

        AssertPage(page1, [1, 2, 3], nextCursor: 3, hasMore: true);
        AssertPage(page2, [4, 5, 6], nextCursor: 6, hasMore: true);
        AssertPage(page3, [7], nextCursor: 7, hasMore: false);
        AssertPage(empty, [], nextCursor: 7, hasMore: false);
    }

    [Fact]
    public async Task Pull_concurrent_with_pushes_never_skips_a_revision()
    {
        var (client, _) = await fixture.NewUserClientAsync();
        const int total = 30;

        var writers = Task.WhenAll(Enumerable.Range(0, 3).Select(async writer =>
        {
            for (var i = 0; i < total / 3; i++)
                await PushOneAsync(client, Upsert(Guid.NewGuid(), null, $"Escritor {writer} #{i}"));
        }));

        var seen = new List<long>();
        long cursor = 0;
        while (seen.Count < total)
        {
            var page = await PullAsync(client, cursor, limit: 4);
            seen.AddRange(page.Changes.Select(c => c.Revision));
            cursor = page.NextCursor;
            if (page.Changes.Count == 0)
                await Task.Delay(10);
        }
        await writers;

        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), seen);
    }

    [Fact]
    public async Task Sync_endpoints_require_authentication()
    {
        var anonymous = fixture.Api.CreateClient();

        var push = await anonymous.PostAsJsonAsync("/api/sync/push", new { deviceId = DeviceId, operations = new[] { Upsert(Guid.NewGuid(), null) } });
        var pull = await anonymous.GetAsync("/api/sync/pull?cursor=0");

        Assert.Equal(HttpStatusCode.Unauthorized, push.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, pull.StatusCode);
    }

    [Fact]
    public async Task Batch_and_pull_parameters_are_validated()
    {
        var (client, _) = await fixture.NewUserClientAsync();
        var tooMany = Enumerable.Range(0, 51).Select(_ => Upsert(Guid.NewGuid(), null)).ToArray();

        var badRequests = new[]
        {
            await client.PostAsJsonAsync("/api/sync/push", new { deviceId = DeviceId, operations = Array.Empty<object>() }),
            await client.PostAsJsonAsync("/api/sync/push", new { deviceId = DeviceId, operations = tooMany }),
            await client.PostAsJsonAsync("/api/sync/push", new { deviceId = Guid.Empty, operations = new[] { Upsert(Guid.NewGuid(), null) } }),
            await client.GetAsync("/api/sync/pull?cursor=-1"),
            await client.GetAsync("/api/sync/pull?cursor=0&limit=0"),
            await client.GetAsync("/api/sync/pull?cursor=0&limit=501"),
        };

        Assert.All(badRequests, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
    }

    [Fact]
    public async Task Push_and_pull_json_follow_the_contract()
    {
        var (client, _) = await fixture.NewUserClientAsync();
        var (other, _) = await fixture.NewUserClientAsync();
        var taskId = Guid.NewGuid();

        var applied = await RawPushAsync(client, Upsert(taskId, null));
        var notFound = await RawPushAsync(other, Upsert(taskId, 1));
        using var pull = JsonDocument.Parse(await client.GetStringAsync("/api/sync/pull?cursor=0"));

        Assert.Equal(["operationId", "serverRevision", "serverTask", "status"], Names(applied));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        Assert.Equal("2026-10-30", applied.GetProperty("serverTask").GetProperty("dueDate").GetString());
        Assert.Equal("medium", applied.GetProperty("serverTask").GetProperty("priority").GetString());

        Assert.Equal(["operationId", "reason", "serverTask", "status"], Names(notFound));
        Assert.Equal(JsonValueKind.Null, notFound.GetProperty("serverTask").ValueKind);

        Assert.Equal(["changes", "hasMore", "nextCursor"], Names(pull.RootElement));
        var change = pull.RootElement.GetProperty("changes")[0];
        Assert.Equal(["action", "revision", "task"], Names(change));
        Assert.Equal(1, change.GetProperty("task").GetProperty("version").GetInt32());
    }

    private static async Task<JsonElement> RawPushAsync(HttpClient client, object operation)
    {
        var response = await client.PostAsJsonAsync("/api/sync/push", new { deviceId = DeviceId, operations = new[] { operation } });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("results")[0].Clone();
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name).Order()];
}
