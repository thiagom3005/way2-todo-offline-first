using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Way2.Todo.Api.Identity;
using Way2.Todo.Tests.Support;

namespace Way2.Todo.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthApiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Health_reports_reachable_database()
    {
        var response = await fixture.Api.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Seeded_user_logs_in_with_case_insensitive_email()
    {
        var response = await fixture.Api.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "  ALICE@test.local ", password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.Equal(ApiFactory.AliceEmail, body.User.Email);
        Assert.NotEqual(Guid.Empty, body.User.Id);
        Assert.InRange(body.ExpiresAt, DateTime.UtcNow.AddMinutes(55), DateTime.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_401()
    {
        var client = fixture.Api.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login",
            new { email = ApiFactory.AliceEmail, password = "wrong-password" });
        var unknownEmail = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "nobody@test.local", password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(await TitleOf(wrongPassword), await TitleOf(unknownEmail));
    }

    [Fact]
    public async Task Missing_credentials_are_a_validation_error()
    {
        var response = await fixture.Api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task A_token_signed_with_another_hmac_algorithm_is_rejected_even_with_the_right_key()
    {
        // 64 bytes: chave válida também para HS512, para que só o algoritmo diferencie os dois tokens.
        const string key = "test-only-signing-key-long-enough-for-hs512-0123456789abcdefghij";
        await using var api = new ApiFactory(fixture.ConnectionString, builder => builder.UseSetting("Jwt:SigningKey", key));
        var user = await fixture.CreateUserAsync();

        async Task<HttpStatusCode> ListTasksWith(string algorithm)
        {
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = ApiFactory.Jwt.Issuer,
                Audience = ApiFactory.Jwt.Audience,
                Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm),
            });
            var client = api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await client.GetAsync("/api/tasks")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await ListTasksWith(SecurityAlgorithms.HmacSha256));
        Assert.Equal(HttpStatusCode.Unauthorized, await ListTasksWith(SecurityAlgorithms.HmacSha512));
    }

    private static async Task<string> TitleOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("title").GetString()!;
    }
}
