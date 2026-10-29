using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Way2.Todo.Tests.Support;

/// <summary>
/// O TestServer não tem conexão TCP (o IP remoto é nulo). Este filtro roda antes de todo o pipeline da API e
/// define o IP do par a partir de um cabeçalho de teste, simulando quem abriu a conexão: o proxy ou um cliente.
/// </summary>
public sealed class PeerAddressFilter : IStartupFilter
{
    public const string Header = "X-Test-Peer-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var peer))
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
            return nextMiddleware(context);
        });
        next(app);
    };
}
