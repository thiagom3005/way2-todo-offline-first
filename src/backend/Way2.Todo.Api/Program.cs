using Way2.Todo.Api.Infrastructure;
using Way2.Todo.Api.Tasks;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Todo")
    ?? throw new InvalidOperationException("ConnectionStrings:Todo não configurada.");

builder.Services.AddDbContext<TodoDbContext>(options => TodoDbContext.Configure(options, connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TaskCommandService>();

var app = builder.Build();

app.Run();

public partial class Program;
