using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Sync;

namespace Way2.Todo.Tests.Support;

/// <summary>Operações de push/pull montadas como JSON cru (permite enviar valores inválidos de propósito).</summary>
public static class SyncOperations
{
    public static readonly Guid DeviceId = Guid.NewGuid();

    /// <summary>Usuário novo com cliente autenticado: revisões e cursor começam do zero.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> NewUserClientAsync(
        this PostgresFixture fixture, WebApplicationFactory<Program>? api = null)
    {
        var user = await fixture.CreateUserAsync();
        var token = new JwtTokenIssuer(Options.Create(ApiFactory.Jwt), TimeProvider.System).Issue(user).AccessToken;
        var client = (api ?? fixture.Api).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, user.Id);
    }

    public static Dictionary<string, object?> Upsert(
        Guid taskId, int? baseVersion, string title = "Conferir medição", Guid? operationId = null,
        string priority = "medium", string? dueDate = "2026-10-30") => new()
    {
        ["operationId"] = operationId ?? Guid.NewGuid(),
        ["taskId"] = taskId,
        ["action"] = "upsert",
        ["baseVersion"] = baseVersion,
        ["payload"] = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = null,
            ["dueDate"] = dueDate,
            ["priority"] = priority,
            ["status"] = "pending",
            ["category"] = "Campo",
            ["createdAt"] = "2026-10-28T12:02:00.000Z",
        },
    };

    public static Dictionary<string, object?> Delete(Guid taskId, int? baseVersion, Guid? operationId = null) => new()
    {
        ["operationId"] = operationId ?? Guid.NewGuid(),
        ["taskId"] = taskId,
        ["action"] = "delete",
        ["baseVersion"] = baseVersion,
        ["payload"] = null,
    };

    public static async Task<PushResult[]> PushAsync(HttpClient client, params object[] operations)
    {
        var response = await client.PostAsJsonAsync("/api/sync/push", new { deviceId = DeviceId, operations });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<PushResponse>(SyncJson.Options))!.Results];
    }

    public static async Task<PushResult> PushOneAsync(HttpClient client, object operation) =>
        Assert.Single(await PushAsync(client, operation));

    public static async Task<PullResponse> PullAsync(HttpClient client, long cursor, int limit = 100)
    {
        var response = await client.GetAsync($"/api/sync/pull?cursor={cursor}&limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PullResponse>(SyncJson.Options))!;
    }
}
