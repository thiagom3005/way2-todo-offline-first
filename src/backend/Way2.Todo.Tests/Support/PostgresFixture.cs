using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Infrastructure;

namespace Way2.Todo.Tests.Support;

/// <summary>PostgreSQL real (Testcontainers) com as migrations aplicadas, compartilhado pela coleção.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ApiFactory? _api;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Uma única instância da API para a coleção (startup aplica migrations e seed de demo).</summary>
    public ApiFactory Api => _api ??= new ApiFactory(ConnectionString);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();
        await _container.DisposeAsync();
    }

    public TodoDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TodoDbContext>();
        TodoDbContext.Configure(options, ConnectionString);
        return new TodoDbContext(options.Options);
    }

    /// <summary>Cria um usuário isolado por teste (sem senha utilizável).</summary>
    public async Task<User> CreateUserAsync()
    {
        await using var db = CreateDbContext();
        var user = User.Create($"{Guid.NewGuid():N}@test.local", DateTime.UtcNow);
        user.SetPasswordHash("unused");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
