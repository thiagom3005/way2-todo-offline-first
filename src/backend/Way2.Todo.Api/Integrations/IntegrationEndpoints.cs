using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Infrastructure;

namespace Way2.Todo.Api.Integrations;

public sealed record OutboxSummary(
    int Pending,
    int Retrying,
    int Delivered,
    double? OldestPendingAgeSeconds,
    int BlockedTasks,
    IReadOnlyList<OutboxEventView> Recent);

public sealed record OutboxEventView(
    Guid EventId,
    string EventType,
    Guid TaskId,
    int TaskVersion,
    long ServerRevision,
    DateTime OccurredAt,
    string Status,
    int Attempts,
    DateTime? NextRetryAt,
    DateTime? LastAttemptAt,
    int? LastStatusCode,
    string? LastError,
    DateTime? DeliveredAt);

public static class IntegrationEndpoints
{
    private const int RecentLimit = 50;

    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        // Acompanhamento da entrega dos eventos do PRÓPRIO usuário (pendentes, em retry, entregues).
        app.MapGet("/api/integrations/outbox", GetOutboxAsync).RequireAuthorization();
        return app;
    }

    private static async Task<OutboxSummary> GetOutboxAsync(
        ClaimsPrincipal principal, TodoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var events = db.OutboxEvents.AsNoTracking().Where(e => e.UserId == userId);
        var pending = events.Where(e => e.Status == OutboxStatuses.Pending);

        var oldestPending = await pending.MinAsync(e => (DateTime?)e.OccurredAt, ct);
        var recent = await events
            .OrderByDescending(e => e.ServerRevision)
            .Take(RecentLimit)
            .Select(e => new OutboxEventView(
                e.EventId, e.EventType, e.TaskId, e.TaskVersion, e.ServerRevision, e.OccurredAt, e.Status,
                e.Attempts, e.Status == OutboxStatuses.Pending ? e.NextRetryAt : null, e.LastAttemptAt,
                e.LastStatusCode, e.LastError, e.DeliveredAt))
            .ToListAsync(ct);

        return new OutboxSummary(
            Pending: await pending.CountAsync(ct),
            Retrying: await pending.CountAsync(e => e.Attempts > 0, ct),
            Delivered: await events.CountAsync(e => e.Status == OutboxStatuses.Delivered, ct),
            OldestPendingAgeSeconds: oldestPending is null
                ? null
                : Math.Round((clock.GetUtcNow().UtcDateTime - oldestPending.Value).TotalSeconds, 1),
            // Tarefas cuja fila está parada num evento que já falhou (bloqueia os posteriores).
            BlockedTasks: await pending.Where(e => e.Attempts > 0).Select(e => e.TaskId).Distinct().CountAsync(ct),
            Recent: recent);
    }
}
