using System.Security.Claims;
using Way2.Todo.Api.Identity;

namespace Way2.Todo.Api.Sync;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        // UserId sempre vem do token; nada no corpo identifica o usuário.
        var sync = app.MapGroup("/api/sync").RequireAuthorization();
        sync.MapPost("/push", PushAsync);
        sync.MapGet("/pull", PullAsync);
        return app;
    }

    private static async Task<IResult> PushAsync(
        PushRequest? request, ClaimsPrincipal principal, SyncService sync, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null || request.DeviceId == Guid.Empty)
            errors["deviceId"] = ["deviceId é obrigatório."];
        if (request?.Operations is not { Count: > 0 and <= SyncLimits.MaxOperationsPerPush })
            errors["operations"] = [$"Envie de 1 a {SyncLimits.MaxOperationsPerPush} operações."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        return TypedResults.Ok(await sync.PushAsync(principal.GetUserId(), request!, ct));
    }

    private static async Task<IResult> PullAsync(
        ClaimsPrincipal principal,
        SyncService sync,
        CancellationToken ct,
        long cursor = 0,
        int limit = SyncLimits.DefaultPullLimit)
    {
        var errors = new Dictionary<string, string[]>();
        if (cursor < 0)
            errors["cursor"] = ["cursor deve ser maior ou igual a 0."];
        if (limit is < 1 or > SyncLimits.MaxPullLimit)
            errors["limit"] = [$"limit deve estar entre 1 e {SyncLimits.MaxPullLimit}."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        return TypedResults.Ok(await sync.PullAsync(principal.GetUserId(), cursor, limit, ct));
    }
}
