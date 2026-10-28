using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Way2.Todo.Api.Infrastructure;

namespace Way2.Todo.Api.Identity;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record LoginResponse(string AccessToken, DateTime ExpiresAt, LoginUser User);

public sealed record LoginUser(Guid Id, string Email);

public static class IdentityModule
{
    private const string LoginRateLimitPolicy = "login";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.Section)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                "Jwt:Issuer e Jwt:Audience são obrigatórios.")
            .Validate(o => Encoding.UTF8.GetByteCount(o.SigningKey) >= 32,
                "Jwt:SigningKey é obrigatória e deve ter pelo menos 32 bytes.")
            .Validate(o => o.AccessTokenMinutes is > 0 and <= 7 * 24 * 60,
                "Jwt:AccessTokenMinutes deve estar entre 1 minuto e 7 dias.")
            .ValidateOnStart();
        services.AddOptions<DemoSeedOptions>().BindConfiguration(DemoSeedOptions.Section);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = jwt.Value.CreateValidationParameters();
            });
        services.AddAuthorization();

        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<JwtTokenIssuer>();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", LoginAsync).RequireRateLimiting(LoginRateLimitPolicy);
        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        TodoDbContext db,
        IPasswordHasher<User> hasher,
        JwtTokenIssuer issuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email)) errors["email"] = ["E-mail é obrigatório."];
        if (string.IsNullOrEmpty(request.Password)) errors["password"] = ["Senha é obrigatória."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var logger = loggerFactory.CreateLogger(typeof(IdentityModule));
        var email = User.NormalizeEmail(request.Email!);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            // Mesmo custo de hash para e-mail inexistente: evita enumerar usuários pelo tempo de resposta.
            hasher.VerifyHashedPassword(TimingDummy.User, TimingDummy.Hash(hasher), request.Password!);
            logger.LogInformation("Falha de autenticação");
            return InvalidCredentials();
        }

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password!) == PasswordVerificationResult.Failed)
        {
            logger.LogInformation("Falha de autenticação");
            return InvalidCredentials();
        }

        var (accessToken, expiresAt) = issuer.Issue(user);
        logger.LogInformation("Usuário {UserId} autenticado", user.Id);
        return Results.Ok(new LoginResponse(accessToken, expiresAt, new LoginUser(user.Id, user.Email)));
    }

    // Resposta idêntica para e-mail inexistente e senha errada.
    private static IResult InvalidCredentials() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Credenciais inválidas.");

    private static class TimingDummy
    {
        public static readonly User User = Identity.User.Create("timing-dummy@invalid", DateTime.UnixEpoch);
        private static string? _hash;

        public static string Hash(IPasswordHasher<User> hasher) =>
            _hash ??= hasher.HashPassword(User, Guid.NewGuid().ToString());
    }
}
