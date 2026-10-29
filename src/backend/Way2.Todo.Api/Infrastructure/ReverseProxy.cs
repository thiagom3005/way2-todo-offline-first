using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Way2.Todo.Api.Infrastructure;

/// <summary>
/// IP real do cliente atrás do proxy reverso (base do rate limit do login). Só os proxies listados em
/// <c>ReverseProxy:KnownProxies</c> — os controlados pela aplicação, como o nginx do compose — são confiáveis.
/// Sem nenhum configurado, o middleware nem entra no pipeline: com as listas vazias ele aceitaria
/// X-Forwarded-For de qualquer origem.
/// </summary>
public static class ReverseProxy
{
    public const string Section = "ReverseProxy";

    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            // Só a entrada mais à direita, acrescentada pelo proxy confiável; as anteriores vieram do cliente.
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in KnownProxies(configuration))
                options.KnownProxies.Add(proxy);
        });
        return services;
    }

    public static WebApplication UseReverseProxySupport(this WebApplication app)
    {
        if (KnownProxies(app.Configuration).Length > 0)
            app.UseForwardedHeaders();
        return app;
    }

    private static IPAddress[] KnownProxies(IConfiguration configuration) =>
    [
        .. (configuration.GetSection($"{Section}:KnownProxies").Get<string[]>() ?? [])
            .Select(value => IPAddress.TryParse(value, out var ip)
                ? ip
                : throw new InvalidOperationException($"{Section}:KnownProxies contém um IP inválido: '{value}'.")),
    ];
}
