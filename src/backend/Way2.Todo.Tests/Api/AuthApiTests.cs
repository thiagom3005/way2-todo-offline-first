using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    private static async Task<string> TitleOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("title").GetString()!;
    }
}
