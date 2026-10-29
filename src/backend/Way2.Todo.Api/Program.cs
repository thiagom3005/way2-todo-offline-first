using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Infrastructure;
using Way2.Todo.Api.Integrations;
using Way2.Todo.Api.Sync;
using Way2.Todo.Api.Tasks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TodoDbContext>((services, options) => TodoDbContext.Configure(
    options,
    services.GetRequiredService<IConfiguration>().GetConnectionString("Todo")
        ?? throw new InvalidOperationException("ConnectionStrings:Todo não configurada.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddIdentityModule();
builder.Services.AddScoped<TaskCommandService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddSingleton<OutboxSignal>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/health", async (TodoDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "healthy" })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Banco de dados indisponível."));
app.MapIdentityEndpoints();
app.MapTaskEndpoints();
app.MapSyncEndpoints();
app.MapIntegrationEndpoints();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<TodoDbContext>().Database.MigrateAsync();
}
await DemoUserSeeder.SeedAsync(app.Services);

app.Run();

public partial class Program;
