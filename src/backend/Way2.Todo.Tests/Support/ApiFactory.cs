using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Way2.Todo.Api.Identity;

namespace Way2.Todo.Tests.Support;

/// <summary>API real em memória apontando para o PostgreSQL do Testcontainers, com configuração explícita de teste.</summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string AliceEmail = "alice@test.local";
    public const string BobEmail = "bob@test.local";
    public const string Password = "test-only-password";

    public static readonly JwtOptions Jwt = new()
    {
        Issuer = "way2-todo-api",
        Audience = "way2-todo-web",
        SigningKey = "test-only-signing-key-with-at-least-32-bytes",
        AccessTokenMinutes = 60,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ambiente próprio: não carrega appsettings.Development.json.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Todo", connectionString);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Jwt:Issuer", Jwt.Issuer);
        builder.UseSetting("Jwt:Audience", Jwt.Audience);
        builder.UseSetting("Jwt:SigningKey", Jwt.SigningKey);
        builder.UseSetting("Jwt:AccessTokenMinutes", Jwt.AccessTokenMinutes.ToString());
        builder.UseSetting("DemoSeed:Enabled", "true");
        builder.UseSetting("DemoSeed:Users:0:Email", AliceEmail);
        builder.UseSetting("DemoSeed:Users:0:Password", Password);
        builder.UseSetting("DemoSeed:Users:1:Email", BobEmail);
        builder.UseSetting("DemoSeed:Users:1:Password", Password);
        // Testes chamam o dispatcher diretamente (DispatchOnceAsync); o loop em segundo plano fica desligado.
        builder.UseSetting("Outbox:DispatcherEnabled", "false");
    }
}
