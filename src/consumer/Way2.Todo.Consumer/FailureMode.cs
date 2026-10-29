namespace Way2.Todo.Consumer;

/// <summary>
/// Falha simulada para demonstrar os retries da API.
/// <list type="bullet">
/// <item><c>StatusCode</c>: responde esse status (sem processar, salvo <c>AfterProcessing</c>).</item>
/// <item><c>Remaining</c>: quantas requisições serão afetadas; <c>null</c> = até ser desligado.</item>
/// <item><c>DelayMs</c>: atraso antes de responder (acima do timeout da API força timeout).</item>
/// <item><c>AfterProcessing</c>: grava o evento e SÓ ENTÃO responde a falha (ack perdido).</item>
/// </list>
/// </summary>
public sealed record FailureSettings(int? StatusCode, int? Remaining, int DelayMs = 0, bool AfterProcessing = false);

public sealed class FailureMode
{
    private readonly object gate = new();
    private FailureSettings? settings;

    public FailureSettings? Current
    {
        get
        {
            lock (gate) return settings;
        }
    }

    public void Set(FailureSettings? value)
    {
        lock (gate) settings = value;
    }

    /// <summary>Falha a aplicar nesta requisição (consome uma de <c>Remaining</c>) ou <c>null</c>.</summary>
    public FailureSettings? Consume()
    {
        lock (gate)
        {
            if (settings is null)
                return null;
            if (settings.Remaining is int remaining)
            {
                var current = settings;
                settings = remaining > 1 ? settings with { Remaining = remaining - 1 } : null;
                return current;
            }
            return settings;
        }
    }
}
