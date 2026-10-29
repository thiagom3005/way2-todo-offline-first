using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Way2.Todo.Tests.Support;

namespace Way2.Todo.Tests.Api;

/// <summary>
/// Rate limit do login atrás do nginx: o IP do cliente vem do X-Forwarded-For SÓ quando a conexão chega do
/// proxy configurado. Cada cenário usa uma instância própria da API (limitador zerado).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReverseProxyTests(PostgresFixture fixture)
{
    private const string TrustedProxy = "10.0.0.10";
    private const int LoginPermitsPerMinute = 20;

    private ApiFactory Api(string? knownProxy) => new(fixture.ConnectionString, builder =>
    {
        if (knownProxy is not null)
            builder.UseSetting("ReverseProxy:KnownProxies:0", knownProxy);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, PeerAddressFilter>());
    });

    /// <summary>Tentativa de login (corpo vazio: 400 barato, mas conta no limite) vinda da conexão <paramref name="peer"/>.</summary>
    private static async Task<HttpStatusCode> LoginAsync(ApiFactory api, string peer, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(PeerAddressFilter.Header, peer);
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        using var response = await api.CreateClient().SendAsync(request);
        return response.StatusCode;
    }

    private static async Task SpendLimitAsync(ApiFactory api, string peer, Func<int, string> forwardedFor)
    {
        for (var i = 0; i < LoginPermitsPerMinute; i++)
            Assert.Equal(HttpStatusCode.BadRequest, await LoginAsync(api, peer, forwardedFor(i)));
    }

    [Fact]
    public async Task Behind_the_trusted_proxy_each_client_has_its_own_login_limit()
    {
        await using var api = Api(TrustedProxy);

        await SpendLimitAsync(api, TrustedProxy, _ => "203.0.113.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api, TrustedProxy, "203.0.113.1"));
        // Outro usuário atrás do mesmo proxy não paga pelo primeiro.
        Assert.Equal(HttpStatusCode.BadRequest, await LoginAsync(api, TrustedProxy, "203.0.113.2"));
    }

    [Fact]
    public async Task Forwarded_for_sent_by_an_untrusted_peer_is_ignored()
    {
        await using var api = Api(TrustedProxy);

        // Cliente falando direto com a API e trocando o cabeçalho a cada tentativa.
        await SpendLimitAsync(api, "198.51.100.7", i => $"203.0.113.{i}");

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api, "198.51.100.7", "203.0.113.200"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api, "198.51.100.7", null));
    }

    [Fact]
    public async Task Addresses_prepended_by_the_client_do_not_create_new_limits()
    {
        await using var api = Api(TrustedProxy);

        // Só a entrada mais à direita (a do proxy) vale; o que o cliente pôs antes é ignorado.
        await SpendLimitAsync(api, TrustedProxy, i => $"192.0.2.{i}, 203.0.113.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api, TrustedProxy, "192.0.2.250, 203.0.113.9"));
    }

    [Fact]
    public async Task Without_a_configured_proxy_no_forwarded_header_is_trusted()
    {
        await using var api = Api(knownProxy: null);

        await SpendLimitAsync(api, TrustedProxy, i => $"203.0.113.{i}");

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api, TrustedProxy, "203.0.113.200"));
    }

    [Fact]
    public void An_invalid_proxy_address_stops_the_startup()
    {
        using var api = Api("nginx");

        var error = Record.Exception(() => api.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("ReverseProxy:KnownProxies contém um IP inválido: 'nginx'", error.ToString());
    }
}
