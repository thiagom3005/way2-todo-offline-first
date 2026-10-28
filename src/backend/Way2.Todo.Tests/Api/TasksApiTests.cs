using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Tasks;
using Way2.Todo.Tests.Support;

namespace Way2.Todo.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TasksApiTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 10, 28, 12, 2, 0, DateTimeKind.Utc);

    private async Task<(HttpClient Client, Guid UserId)> LoginAsync(string email)
    {
        var client = fixture.Api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password });
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (client, login.User.Id);
    }

    private async Task<Guid> CreateTaskAsync(Guid userId, string title, DateOnly? dueDate = null)
    {
        await using var db = fixture.CreateDbContext();
        var service = new TaskCommandService(db, TimeProvider.System);
        var result = await service.CreateAsync(userId, Guid.NewGuid(),
            new TaskFields(title, null, dueDate, TaskPriority.High, TodoTaskStatus.Pending, "Campo"), CreatedAt);
        return Assert.IsType<TaskCommandResult.Applied>(result).Task.Id;
    }

    private static async Task<JsonElement[]> GetTasksAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tasks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public async Task Tasks_require_a_valid_token()
    {
        var anonymous = fixture.Api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tasks")).StatusCode);

        var user = User.Create(ApiFactory.AliceEmail, DateTime.UtcNow);
        var wrongKey = new JwtOptions
        {
            Issuer = ApiFactory.Jwt.Issuer,
            Audience = ApiFactory.Jwt.Audience,
            SigningKey = "another-signing-key-that-is-long-enough!!",
            AccessTokenMinutes = 60,
        };
        var forged = new JwtTokenIssuer(Options.Create(wrongKey), TimeProvider.System).Issue(user).AccessToken;
        var expired = new JwtTokenIssuer(Options.Create(ApiFactory.Jwt), new FixedTimeProvider(DateTime.UtcNow.AddHours(-3)))
            .Issue(user).AccessToken;

        foreach (var token in new[] { "not-a-jwt", forged, expired })
        {
            var client = fixture.Api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks")).StatusCode);
        }
    }

    [Fact]
    public async Task Each_user_sees_only_own_active_tasks()
    {
        var (alice, aliceId) = await LoginAsync(ApiFactory.AliceEmail);
        var (bob, bobId) = await LoginAsync(ApiFactory.BobEmail);
        var aliceTask = await CreateTaskAsync(aliceId, "Tarefa da Alice");
        var aliceDeleted = await CreateTaskAsync(aliceId, "Excluída da Alice");
        var bobTask = await CreateTaskAsync(bobId, "Tarefa do Bob");
        await using (var db = fixture.CreateDbContext())
            Assert.IsType<TaskCommandResult.Applied>(
                await new TaskCommandService(db, TimeProvider.System).DeleteAsync(aliceId, aliceDeleted, 1));

        var aliceIds = (await GetTasksAsync(alice)).Select(t => t.GetProperty("id").GetGuid()).ToList();
        var bobIds = (await GetTasksAsync(bob)).Select(t => t.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(aliceTask, aliceIds);
        Assert.DoesNotContain(aliceDeleted, aliceIds);
        Assert.DoesNotContain(bobTask, aliceIds);
        Assert.Contains(bobTask, bobIds);
        Assert.DoesNotContain(aliceTask, bobIds);
    }

    [Fact]
    public async Task Task_json_follows_the_contract()
    {
        var (client, userId) = await LoginAsync(ApiFactory.BobEmail);
        var taskId = await CreateTaskAsync(userId, "Formato do contrato", new DateOnly(2026, 10, 30));

        var task = (await GetTasksAsync(client)).Single(t => t.GetProperty("id").GetGuid() == taskId);

        string[] contractFields =
            ["id", "title", "description", "dueDate", "priority", "status", "category", "version", "createdAt", "updatedAt", "deletedAt"];
        Assert.Equal(contractFields.Order(), task.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("2026-10-30", task.GetProperty("dueDate").GetString());
        Assert.Equal("high", task.GetProperty("priority").GetString());
        Assert.Equal("pending", task.GetProperty("status").GetString());
        Assert.Equal(1, task.GetProperty("version").GetInt32());
        Assert.Equal("2026-10-28T12:02:00Z", task.GetProperty("createdAt").GetString());
        Assert.EndsWith("Z", task.GetProperty("updatedAt").GetString());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("deletedAt").ValueKind);
    }
}
