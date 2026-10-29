using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Way2.Todo.Api.Integrations;

public sealed class WebhookOptions
{
    public const string Section = "Webhook";

    /// <summary>Endpoint do consumidor. Sem URL, os eventos ficam pendentes na outbox (nada se perde).</summary>
    public string? Url { get; set; }

    /// <summary>Segredo HMAC compartilhado com o consumidor (mínimo 32 bytes); só via configuração.</summary>
    public string? Secret { get; set; }

    public double TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    public bool DispatcherEnabled { get; set; } = true;
    public double PollIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 20;

    /// <summary>Reserva de um evento por um ciclo; renovada antes de cada envio. Deve passar o timeout HTTP.</summary>
    public double LeaseSeconds { get; set; } = 30;

    public double BaseBackoffSeconds { get; set; } = 2;
    public double MaxBackoffSeconds { get; set; } = 300;
}

/// <summary>Assinatura dos webhooks: <c>t=&lt;unix&gt;,v1=&lt;hex HMAC-SHA256(segredo, "t." + corpo)&gt;</c>.</summary>
public static class WebhookSignature
{
    public const string Header = "X-Way2-Signature";

    public static string Create(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }
}

public static class IntegrationsModule
{
    public static IServiceCollection AddIntegrationsModule(this IServiceCollection services)
    {
        services.AddSingleton<OutboxSignal>();

        services.AddOptions<WebhookOptions>()
            .BindConfiguration(WebhookOptions.Section)
            .Validate(
                o => !o.IsConfigured || (Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"),
                "Webhook:Url deve ser uma URL http(s) absoluta.")
            .Validate(
                o => !o.IsConfigured || Encoding.UTF8.GetByteCount(o.Secret ?? string.Empty) >= 32,
                "Webhook:Secret é obrigatório (mínimo 32 bytes) quando Webhook:Url está configurada.")
            .Validate(o => o.TimeoutSeconds is > 0 and <= 120, "Webhook:TimeoutSeconds deve estar entre 0 e 120.")
            .ValidateOnStart();

        services.AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.Section)
            .Validate(o => o.BatchSize is >= 1 and <= 200, "Outbox:BatchSize deve estar entre 1 e 200.")
            .Validate(o => o.PollIntervalSeconds > 0 && o.BaseBackoffSeconds > 0 && o.MaxBackoffSeconds >= o.BaseBackoffSeconds,
                "Intervalos da outbox inválidos.")
            .Validate<IOptions<WebhookOptions>>(
                (o, webhook) => o.LeaseSeconds >= 2 * webhook.Value.TimeoutSeconds,
                "Outbox:LeaseSeconds deve ser pelo menos o dobro de Webhook:TimeoutSeconds.")
            .ValidateOnStart();

        // Sem seguir redirects: um 3xx transformaria o POST em GET e um 2xx nele marcaria como entregue.
        // Sem handler de resiliência: os retries são da outbox (duráveis), não do HttpClient.
        services.AddHttpClient(OutboxDispatcher.HttpClientName, (provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(
                    provider.GetRequiredService<IOptions<WebhookOptions>>().Value.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddHostedService<OutboxDispatcher>();
        return services;
    }
}
