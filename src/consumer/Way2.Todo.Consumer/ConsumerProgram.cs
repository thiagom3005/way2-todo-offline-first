using System.Text;
using Microsoft.Extensions.Options;

namespace Way2.Todo.Consumer;

public sealed class ConsumerOptions
{
    public const string Section = "Consumer";

    /// <summary>Segredo HMAC compartilhado com a API (mínimo 32 bytes); só via configuração.</summary>
    public string? WebhookSecret { get; set; }

    public string DataPath { get; set; } = "data/events.jsonl";
    public int SignatureToleranceSeconds { get; set; } = 300;
}

/// <summary>
/// Consumidor externo simulado: recebe os webhooks da Todo List, deduplica por eventId e mantém uma
/// representação somente leitura. Não tem credenciais de banco nem token da API — não altera tarefas.
/// </summary>
public sealed class ConsumerProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOptions<ConsumerOptions>()
            .BindConfiguration(ConsumerOptions.Section)
            .Validate(
                o => Encoding.UTF8.GetByteCount(o.WebhookSecret ?? string.Empty) >= 32,
                "Consumer:WebhookSecret é obrigatório (mínimo 32 bytes).")
            .ValidateOnStart();
        builder.Services.AddSingleton(provider => new EventStore(
            provider.GetRequiredService<IOptions<ConsumerOptions>>().Value.DataPath,
            provider.GetRequiredService<ILogger<EventStore>>()));
        builder.Services.AddSingleton<FailureMode>();
        builder.Services.AddSingleton(TimeProvider.System);

        var app = builder.Build();
        app.Services.GetRequiredService<EventStore>(); // carrega o log já no startup (falha cedo se corrompido)

        app.MapPost("/webhooks/todo-events", ReceiveAsync);
        app.MapGet("/events", (EventStore store) => store.Events());
        app.MapGet("/tasks", (EventStore store) => store.Tasks());
        app.MapGet("/failure-mode", (FailureMode failures) =>
            failures.Current is { } current ? Results.Ok(current) : Results.NoContent());
        app.MapPut("/failure-mode", (FailureSettings settings, FailureMode failures) =>
        {
            failures.Set(settings);
            return Results.Ok(settings);
        });
        app.MapDelete("/failure-mode", (FailureMode failures) =>
        {
            failures.Set(null);
            return Results.NoContent();
        });
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html; charset=utf-8"));
        app.Run();
    }

    private static async Task<IResult> ReceiveAsync(
        HttpRequest request,
        EventStore store,
        FailureMode failures,
        IOptions<ConsumerOptions> options,
        TimeProvider clock,
        ILogger<ConsumerProgram> logger,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();
        var eventIdHeader = request.Headers["X-Way2-Event-Id"].ToString();

        // Assinatura sobre os bytes crus, antes de qualquer desserialização.
        var settings = options.Value;
        if (!WebhookSignature.IsValid(request.Headers[WebhookSignature.Header], body, settings.WebhookSecret!,
                clock.GetUtcNow(), settings.SignatureToleranceSeconds))
        {
            logger.LogError("Webhook {EventId} recusado: assinatura ausente, inválida ou fora da janela de tempo.", eventIdHeader);
            return Results.Unauthorized();
        }

        var failure = failures.Consume();
        if (failure is { DelayMs: > 0 })
            await Task.Delay(failure.DelayMs, ct);
        if (failure is { StatusCode: int status, AfterProcessing: false })
        {
            logger.LogWarning("Falha simulada: webhook {EventId} respondido com HTTP {StatusCode} sem processar.", eventIdHeader, status);
            return Results.StatusCode(status);
        }

        if (IncomingEvent.TryParse(body, eventIdHeader) is not { } incoming)
        {
            logger.LogError("Webhook {EventId} recusado: payload fora do contrato v1.", eventIdHeader);
            return Results.BadRequest();
        }

        var isNew = store.Accept(incoming, clock.GetUtcNow(), out var stored);
        logger.LogInformation(
            "Webhook {EventId} ({EventType}) da tarefa {TaskId} v{TaskVersion}: {Result} (entrega {Deliveries})",
            incoming.EventId, incoming.EventType, incoming.TaskId, incoming.TaskVersion,
            isNew ? "registrado" : "duplicado, ignorado", stored.Deliveries);

        if (failure is { StatusCode: int lostAck, AfterProcessing: true })
        {
            logger.LogWarning("Falha simulada: webhook {EventId} processado, mas respondido com HTTP {StatusCode} (ack perdido).", incoming.EventId, lostAck);
            return Results.StatusCode(lostAck);
        }
        return Results.Ok(new { eventId = incoming.EventId, duplicate = !isNew });
    }
}
