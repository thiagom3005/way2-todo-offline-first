using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Infrastructure;
using Way2.Todo.Api.Tasks;
using Way2.Todo.Tests.Support;

namespace Way2.Todo.Tests.Tasks;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TaskCommandServiceTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedOffline = new(2026, 10, 28, 9, 30, 0, 250, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static TaskFields Fields(string title = "Conferir medição") =>
        new(title, "Unidade A", new DateOnly(2026, 10, 30), TaskPriority.High, TodoTaskStatus.Pending, "Campo");

    private static TaskCommandService Service(TodoDbContext db) => new(db, new FixedTimeProvider(Now));

    private async Task<TaskSnapshot> CreateAsync(Guid userId, Guid? taskId = null)
    {
        await using var db = fixture.CreateDbContext();
        var result = await Service(db).CreateAsync(userId, taskId ?? Guid.NewGuid(), Fields(), CreatedOffline);
        return Assert.IsType<TaskCommandResult.Applied>(result).Task;
    }

    private async Task<TodoTask> LoadAsync(Guid taskId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
    }

    [Fact]
    public async Task Create_persists_version_1_date_only_and_client_created_at()
    {
        var user = await fixture.CreateUserAsync();

        var snapshot = await CreateAsync(user.Id);
        var stored = await LoadAsync(snapshot.Id);

        Assert.Equal(1, stored.Version);
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal(new DateOnly(2026, 10, 30), stored.DueDate);
        Assert.Equal(CreatedOffline, stored.CreatedAt);
        Assert.Equal(Now, stored.UpdatedAt);
        Assert.Equal(snapshot, TaskSnapshot.From(stored));
    }

    [Fact]
    public async Task Create_with_existing_id_of_same_user_returns_conflict_with_snapshot()
    {
        var user = await fixture.CreateUserAsync();
        var existing = await CreateAsync(user.Id);

        await using var db = fixture.CreateDbContext();
        var result = await Service(db).CreateAsync(user.Id, existing.Id, Fields("Outro"), CreatedOffline);

        var conflict = Assert.IsType<TaskCommandResult.Conflict>(result);
        Assert.Equal(ConflictReasons.AlreadyExists, conflict.Reason);
        Assert.Equal(existing, conflict.ServerTask);
    }

    [Fact]
    public async Task Create_with_id_owned_by_other_user_returns_conflict_without_data()
    {
        var owner = await fixture.CreateUserAsync();
        var intruder = await fixture.CreateUserAsync();
        var ownersTask = await CreateAsync(owner.Id);

        await using var db = fixture.CreateDbContext();
        var result = await Service(db).CreateAsync(intruder.Id, ownersTask.Id, Fields("Tentativa"), CreatedOffline);

        var conflict = Assert.IsType<TaskCommandResult.Conflict>(result);
        Assert.Null(conflict.ServerTask);
        Assert.Equal("Conferir medição", (await LoadAsync(ownersTask.Id)).Title);
    }

    [Fact]
    public async Task Update_with_current_base_version_increments_version()
    {
        var user = await fixture.CreateUserAsync();
        var created = await CreateAsync(user.Id);

        await using var db = fixture.CreateDbContext();
        var result = await Service(db).UpdateAsync(user.Id, created.Id, baseVersion: 1,
            Fields("Atualizada") with { Status = TodoTaskStatus.Completed, DueDate = null });

        var applied = Assert.IsType<TaskCommandResult.Applied>(result);
        Assert.Equal(2, applied.Task.Version);
        var stored = await LoadAsync(created.Id);
        Assert.Equal(2, stored.Version);
        Assert.Equal("Atualizada", stored.Title);
        Assert.Equal(TodoTaskStatus.Completed, stored.Status);
        Assert.Null(stored.DueDate);
        Assert.Equal(CreatedOffline, stored.CreatedAt);
    }

    [Fact]
    public async Task Update_with_stale_base_version_returns_conflict_and_does_not_overwrite()
    {
        var user = await fixture.CreateUserAsync();
        var created = await CreateAsync(user.Id);
        await using (var first = fixture.CreateDbContext())
            Assert.IsType<TaskCommandResult.Applied>(
                await Service(first).UpdateAsync(user.Id, created.Id, 1, Fields("Dispositivo A")));

        await using var second = fixture.CreateDbContext();
        var result = await Service(second).UpdateAsync(user.Id, created.Id, 1, Fields("Dispositivo B"));

        var conflict = Assert.IsType<TaskCommandResult.Conflict>(result);
        Assert.Equal(ConflictReasons.VersionMismatch, conflict.Reason);
        Assert.Equal("Dispositivo A", conflict.ServerTask?.Title);
        Assert.Equal(2, conflict.ServerTask?.Version);
        Assert.Equal("Dispositivo A", (await LoadAsync(created.Id)).Title);
    }

    [Fact]
    public async Task Concurrent_updates_from_same_version_apply_exactly_once()
    {
        var user = await fixture.CreateUserAsync();
        var created = await CreateAsync(user.Id);

        async Task<TaskCommandResult> UpdateFrom(string title)
        {
            await using var db = fixture.CreateDbContext();
            return await Service(db).UpdateAsync(user.Id, created.Id, 1, Fields(title));
        }

        var results = await Task.WhenAll(UpdateFrom("A"), UpdateFrom("B"));

        Assert.Single(results.OfType<TaskCommandResult.Applied>());
        Assert.Single(results.OfType<TaskCommandResult.Conflict>());
        Assert.Equal(2, (await LoadAsync(created.Id)).Version);
    }

    [Fact]
    public async Task Delete_keeps_tombstone_and_rejects_later_changes()
    {
        var user = await fixture.CreateUserAsync();
        var created = await CreateAsync(user.Id);

        await using (var db = fixture.CreateDbContext())
        {
            var deleted = Assert.IsType<TaskCommandResult.Applied>(await Service(db).DeleteAsync(user.Id, created.Id, 1));
            Assert.Equal(2, deleted.Task.Version);
            Assert.Equal(Now, deleted.Task.DeletedAt);
        }

        var stored = await LoadAsync(created.Id);
        Assert.Equal(Now, stored.DeletedAt);

        await using var again = fixture.CreateDbContext();
        var update = await Service(again).UpdateAsync(user.Id, created.Id, 2, Fields("Ressuscitar"));
        var conflict = Assert.IsType<TaskCommandResult.Conflict>(update);
        Assert.Equal(ConflictReasons.Deleted, conflict.Reason);
        Assert.NotNull(conflict.ServerTask?.DeletedAt);
    }

    [Fact]
    public async Task Other_user_cannot_update_or_delete_and_receives_no_data()
    {
        var owner = await fixture.CreateUserAsync();
        var intruder = await fixture.CreateUserAsync();
        var ownersTask = await CreateAsync(owner.Id);

        await using var db = fixture.CreateDbContext();
        var service = Service(db);
        var update = await service.UpdateAsync(intruder.Id, ownersTask.Id, 1, Fields("Invasão"));
        var delete = await service.DeleteAsync(intruder.Id, ownersTask.Id, 1);

        foreach (var result in new[] { update, delete })
        {
            var conflict = Assert.IsType<TaskCommandResult.Conflict>(result);
            Assert.Equal(ConflictReasons.NotFound, conflict.Reason);
            Assert.Null(conflict.ServerTask);
        }

        var stored = await LoadAsync(ownersTask.Id);
        Assert.Equal(1, stored.Version);
        Assert.Null(stored.DeletedAt);
    }

    [Fact]
    public async Task Invalid_fields_return_errors_and_persist_nothing()
    {
        var user = await fixture.CreateUserAsync();
        var taskId = Guid.NewGuid();
        var unspecified = DateTime.SpecifyKind(CreatedOffline, DateTimeKind.Unspecified);

        await using var db = fixture.CreateDbContext();
        var result = await Service(db).CreateAsync(user.Id, taskId, Fields(" ") with { Category = "" }, unspecified);

        var invalid = Assert.IsType<TaskCommandResult.Invalid>(result);
        Assert.Equal(["category", "createdAt", "title"], invalid.Errors.Keys.Order());
        Assert.False(await db.Tasks.AnyAsync(t => t.Id == taskId));
    }
}
