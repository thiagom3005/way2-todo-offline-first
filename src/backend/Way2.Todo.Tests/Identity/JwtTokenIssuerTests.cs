using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Way2.Todo.Api.Identity;
using Way2.Todo.Tests.Support;

namespace Way2.Todo.Tests.Identity;

public sealed class JwtTokenIssuerTests
{
    private static readonly JwtOptions Options = new()
    {
        Issuer = "issuer",
        Audience = "audience",
        SigningKey = "unit-test-signing-key-with-32-bytes-min",
        AccessTokenMinutes = 60,
    };

    [Fact]
    public async Task Issued_token_is_valid_and_carries_user_identity()
    {
        var now = DateTime.UtcNow;
        var user = User.Create(" Pessoa@Test.Local ", now);
        var issuer = new JwtTokenIssuer(Microsoft.Extensions.Options.Options.Create(Options), new FixedTimeProvider(now));

        var (token, expiresAt) = issuer.Issue(user);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, Options.CreateValidationParameters());

        Assert.True(validation.IsValid);
        Assert.Equal(user.Id.ToString(), validation.Claims[JwtRegisteredClaimNames.Sub]);
        Assert.Equal("pessoa@test.local", validation.Claims[JwtRegisteredClaimNames.Email]);
        Assert.Equal(expiresAt, ((JsonWebToken)validation.SecurityToken).ValidTo);
        Assert.Equal(0, expiresAt.Ticks % TimeSpan.TicksPerSecond);
        Assert.InRange(expiresAt, now.AddMinutes(60).AddSeconds(-1), now.AddMinutes(60));
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var past = DateTime.UtcNow.AddHours(-2);
        var issuer = new JwtTokenIssuer(Microsoft.Extensions.Options.Options.Create(Options), new FixedTimeProvider(past));

        var (token, _) = issuer.Issue(User.Create("x@test.local", past));
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, Options.CreateValidationParameters());

        Assert.False(validation.IsValid);
    }
}
