using System.Threading.Channels;

namespace Way2.Todo.Api.Integrations;

/// <summary>
/// Acorda o dispatcher logo após um commit com evento novo (entrega quase em tempo real).
/// Capacidade 1 e <c>DropWrite</c>: avisar nunca bloqueia nem lança — um push já confirmado não pode
/// falhar por causa do aviso. Avisos perdidos não importam: o dispatcher também faz polling.
/// </summary>
public sealed class OutboxSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => channel.Writer.TryWrite(true);

    /// <summary>Espera um aviso ou até <paramref name="timeout"/> (o que vier antes).</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            await channel.Reader.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Tempo do polling esgotado sem aviso.
        }
    }
}
