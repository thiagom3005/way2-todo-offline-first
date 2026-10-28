using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Way2.Todo.Api.Infrastructure;

namespace Way2.Todo.Api.Identity;

public sealed class DemoSeedOptions
{
    public const string Section = "DemoSeed";

    public bool Enabled { get; set; }
    public List<DemoUserOptions> Users { get; set; } = [];
}

public sealed class DemoUserOptions
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Cria/atualiza usuários de demonstração a partir de configuração explícita (desligado por padrão).
/// Senhas são persistidas apenas como hash; a configuração é a fonte da verdade da senha de demo.
/// </summary>
public static class DemoUserSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var options = provider.GetRequiredService<IOptions<DemoSeedOptions>>().Value;
        if (!options.Enabled)
            return;

        var db = provider.GetRequiredService<TodoDbContext>();
        var hasher = provider.GetRequiredService<IPasswordHasher<User>>();
        var clock = provider.GetRequiredService<TimeProvider>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DemoUserSeeder));

        foreach (var demo in options.Users)
        {
            if (string.IsNullOrWhiteSpace(demo.Email) || string.IsNullOrEmpty(demo.Password))
                throw new InvalidOperationException("DemoSeed:Users exige Email e Password.");

            var email = User.NormalizeEmail(demo.Email);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null)
            {
                user = User.Create(email, clock.GetUtcNow().UtcDateTime);
                db.Users.Add(user);
            }
            else if (hasher.VerifyHashedPassword(user, user.PasswordHash, demo.Password) != PasswordVerificationResult.Failed)
            {
                continue;
            }

            user.SetPasswordHash(hasher.HashPassword(user, demo.Password));
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Usuário de demonstração {UserId} provisionado", user.Id);
        }
    }
}
