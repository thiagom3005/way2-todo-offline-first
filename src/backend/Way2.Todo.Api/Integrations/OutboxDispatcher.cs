using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Way2.Todo.Api.Infrastructure;

namespace Way2.Todo.Api.Integrations;

/// <summary>
/// Entrega os eventos da outbox por webhook, FORA da transação de negócio. Garantias:
/// at-least-once (o consumidor deduplica por eventId); despacho em ordem por tarefa (um evento só sai
/// quando não há nenhum anterior da mesma tarefa pendente — em backoff, retry ou reservado);
/// retry com backoff exponencial limitado e sem descarte; retomada após reinício (tudo está no banco).
/// Todo instante vem do <see cref="TimeProvider"/> da aplicação, nunca do relógio do PostgreSQL.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    OutboxSignal signal,
    IOptions<OutboxOptions> outboxOptions,
    IOptions<WebhookOptions> webhookOptions,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger,
    Func<double>? jitter = null) : BackgroundService
{
    public const string HttpClientName = "webhook";
    public const string EventIdHeader = "X-Way2-Event-Id";
    public const string EventTypeHeader = "X-Way2-Event-Type";

    private readonly Func<double> jitter = jitter ?? Random.Shared.NextDouble;

    /// <summary>Backoff exponencial (expoente limitado) com jitter de ±20% e teto.</summary>
    public static TimeSpan BackoffDelay(int attempts, OutboxOptions options, double jitter)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 16);
        var seconds = Math.Min(options.BaseBackoffSeconds * Math.Pow(2, exponent), options.MaxBackoffSeconds);
        return TimeSpan.FromSeconds(Math.Min(seconds * (0.8 + 0.4 * jitter), options.MaxBackoffSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // não segura o startup do host
        if (!outboxOptions.Value.DispatcherEnabled)
            return;
        if (!webhookOptions.Value.IsConfigured)
        {
            logger.LogWarning("Webhook:Url não configurada: os eventos permanecem pendentes na outbox.");
            return;
        }

        var poll = TimeSpan.FromSeconds(outboxOptions.Value.PollIntervalSeconds);
        logger.LogInformation("Dispatcher da outbox iniciado (polling a cada {PollSeconds}s).", poll.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DispatchOnceAsync(stoppingToken) > 0)
                    continue; // lote não vazio: pode haver mais eventos elegíveis agora
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // desligamento: reservas expiram e o próximo processo retoma
            }
            catch (Exception ex)
            {
                // Falha de banco/rede no ciclo nunca derruba a API: tenta de novo no próximo intervalo.
                logger.LogError(ex, "Ciclo da outbox falhou; nova tentativa no próximo intervalo.");
            }
            await signal.WaitAsync(poll, stoppingToken);
        }
    }

    /// <summary>Um ciclo: reserva as cabeças elegíveis e tenta entregá-las. Devolve quantas reservou.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken ct = default)
    {
        var outbox = outboxOptions.Value;
        var webhook = webhookOptions.Value;
        var claimToken = Guid.NewGuid();
        var claimed = await ClaimAsync(claimToken, outbox, ct);
        if (claimed.Count == 0)
            return 0;

        var http = httpClients.CreateClient(HttpClientName);
        for (var i = 0; i < claimed.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var outboxEvent = claimed[i];
            // A reserva pode ter expirado (ciclo lento): se outro ciclo assumiu o evento, não envia.
            if (!await RenewLeaseAsync(outboxEvent.EventId, claimToken, outbox, ct))
                continue;

            var attempt = await SendAsync(http, outboxEvent, webhook, ct);
            await RecordAsync(outboxEvent, claimToken, attempt, outbox, ct);
            if (attempt.Unreachable)
            {
                // Consumidor inalcançável: não gasta tentativas dos demais nem prende o ciclo em timeouts.
                await ReleaseAsync(claimed.Skip(i + 1).Select(e => e.EventId).ToList(), claimToken, ct);
                break;
            }
        }
        return claimed.Count;
    }

    private async Task<List<OutboxEvent>> ClaimAsync(Guid claimToken, OutboxOptions outbox, CancellationToken ct)
    {
        var now = Now();
        var leaseUntil = now.AddSeconds(outbox.LeaseSeconds);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        // Elegível = cabeça da tarefa: nenhum evento ANTERIOR da mesma tarefa ainda 'pending' (seja em
        // backoff, em retry ou reservado por outro ciclo). SKIP LOCKED permite mais de uma instância.
        var claimed = await db.OutboxEvents.FromSql($"""
            WITH candidates AS (
                SELECT o.event_id
                FROM integration_outbox o
                WHERE o.status = 'pending'
                  AND o.next_retry_at <= {now}
                  AND (o.locked_until IS NULL OR o.locked_until <= {now})
                  AND NOT EXISTS (
                      SELECT 1 FROM integration_outbox earlier
                      WHERE earlier.task_id = o.task_id
                        AND earlier.status = 'pending'
                        AND earlier.server_revision < o.server_revision)
                ORDER BY o.next_retry_at, o.server_revision
                LIMIT {outbox.BatchSize}
                FOR UPDATE SKIP LOCKED)
            UPDATE integration_outbox AS e
            SET locked_until = {leaseUntil}, claim_token = {claimToken}
            FROM candidates
            WHERE e.event_id = candidates.event_id
            RETURNING e.*
            """).AsNoTracking().ToListAsync(ct);
        return [.. claimed.OrderBy(e => e.NextRetryAt).ThenBy(e => e.ServerRevision)];
    }

    private async Task<bool> RenewLeaseAsync(Guid eventId, Guid claimToken, OutboxOptions outbox, CancellationToken ct)
    {
        var leaseUntil = Now().AddSeconds(outbox.LeaseSeconds);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        var renewed = await db.OutboxEvents
            .Where(e => e.EventId == eventId && e.ClaimToken == claimToken && e.Status == OutboxStatuses.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntil, leaseUntil), ct);
        return renewed == 1;
    }

    private async Task<DeliveryAttempt> SendAsync(
        HttpClient http, OutboxEvent outboxEvent, WebhookOptions webhook, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(outboxEvent.PayloadJson);
        // Timestamp e assinatura novos a cada tentativa; eventId e bytes do payload são sempre os mesmos.
        var signature = WebhookSignature.Create(webhook.Secret!, clock.GetUtcNow().ToUnixTimeSeconds(), body);
        using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Add(EventIdHeader, outboxEvent.EventId.ToString());
        request.Headers.Add(EventTypeHeader, outboxEvent.EventType);
        request.Headers.Add(WebhookSignature.Header, signature);

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            return new DeliveryAttempt(status is >= 200 and < 300, status, $"HTTP {status}", false, Elapsed(started));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryAttempt(false, null, "Timeout", true, Elapsed(started));
        }
        catch (HttpRequestException ex)
        {
            return new DeliveryAttempt(false, null, $"Falha de conexão ({ex.HttpRequestError})", true, Elapsed(started));
        }
    }

    private async Task RecordAsync(
        OutboxEvent outboxEvent, Guid claimToken, DeliveryAttempt attempt, OutboxOptions outbox, CancellationToken ct)
    {
        var now = Now();
        var attempts = outboxEvent.Attempts + 1;
        var nextRetryAt = now + BackoffDelay(attempts, outbox, jitter());
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        var owned = db.OutboxEvents.Where(e => e.EventId == outboxEvent.EventId && e.ClaimToken == claimToken);

        var updated = attempt.Delivered
            ? await owned.ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, OutboxStatuses.Delivered)
                .SetProperty(e => e.DeliveredAt, now)
                .SetProperty(e => e.Attempts, attempts)
                .SetProperty(e => e.LastAttemptAt, now)
                .SetProperty(e => e.LastStatusCode, attempt.StatusCode)
                .SetProperty(e => e.LastError, (string?)null)
                .SetProperty(e => e.LockedUntil, (DateTime?)null)
                .SetProperty(e => e.ClaimToken, (Guid?)null), ct)
            : await owned.ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Attempts, attempts)
                .SetProperty(e => e.LastAttemptAt, now)
                .SetProperty(e => e.LastStatusCode, attempt.StatusCode)
                .SetProperty(e => e.LastError, attempt.Error)
                .SetProperty(e => e.NextRetryAt, nextRetryAt)
                .SetProperty(e => e.LockedUntil, (DateTime?)null)
                .SetProperty(e => e.ClaimToken, (Guid?)null), ct);

        if (updated == 0)
        {
            logger.LogWarning(
                "Resultado do webhook {EventId} descartado: a reserva expirou e outro ciclo assumiu o evento.",
                outboxEvent.EventId);
        }
        else if (attempt.Delivered)
        {
            logger.LogInformation(
                "Webhook {EventId} ({EventType}) da tarefa {TaskId} entregue na tentativa {Attempt}: {Result} em {ElapsedMs} ms",
                outboxEvent.EventId, outboxEvent.EventType, outboxEvent.TaskId, attempts, attempt.Error, attempt.ElapsedMs);
        }
        else
        {
            // Falha persistente vira Error periodicamente (alerta); 401/403 indicam configuração errada.
            var level = attempts % 10 == 0 || attempt.StatusCode is 401 or 403 ? LogLevel.Error : LogLevel.Warning;
            logger.Log(level,
                "Webhook {EventId} ({EventType}) da tarefa {TaskId} falhou na tentativa {Attempt}: {Result} em {ElapsedMs} ms; próxima tentativa em {NextRetryAt:o}",
                outboxEvent.EventId, outboxEvent.EventType, outboxEvent.TaskId, attempts, attempt.Error, attempt.ElapsedMs, nextRetryAt);
        }
    }

    private async Task ReleaseAsync(List<Guid> eventIds, Guid claimToken, CancellationToken ct)
    {
        if (eventIds.Count == 0)
            return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        await db.OutboxEvents
            .Where(e => eventIds.Contains(e.EventId) && e.ClaimToken == claimToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.LockedUntil, (DateTime?)null)
                .SetProperty(e => e.ClaimToken, (Guid?)null), ct);
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private sealed record DeliveryAttempt(bool Delivered, int? StatusCode, string Error, bool Unreachable, long ElapsedMs);
}
