using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Way2.Todo.Api.Identity;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>Chave HMAC; obrigatória via configuração/variável de ambiente, nunca versionada para produção.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 480;

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));

    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = CreateSigningKey(),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        // Só o algoritmo que a API emite: um token com outro "alg" é recusado mesmo com a chave certa.
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}
