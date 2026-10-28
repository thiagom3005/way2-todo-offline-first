using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Tests.Tasks;

public sealed class TodoTaskTests
{
    private static readonly DateTime CreatedOffline = new(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static TaskFields Fields(string title = "Conferir medição") =>
        new(title, null, new DateOnly(2026, 10, 30), TaskPriority.Medium, TodoTaskStatus.Pending, "Campo");

    private static TodoTask NewTask() => TodoTask.Create(Guid.NewGuid(), Guid.NewGuid(), Fields(), CreatedOffline, Now);

    [Fact]
    public void Create_starts_at_version_1_and_keeps_client_created_at()
    {
        var task = TodoTask.Create(Guid.NewGuid(), Guid.NewGuid(), Fields("  Título  "), CreatedOffline, Now);

        Assert.Equal(1, task.Version);
        Assert.Equal("Título", task.Title);
        Assert.Equal(CreatedOffline, task.CreatedAt);
        Assert.Equal(Now, task.UpdatedAt);
        Assert.Null(task.DeletedAt);
        Assert.Equal(new DateOnly(2026, 10, 30), task.DueDate);
    }

    [Fact]
    public void Create_rejects_non_utc_created_at()
    {
        var local = new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified);

        Assert.Throws<ArgumentException>(() => TodoTask.Create(Guid.NewGuid(), Guid.NewGuid(), Fields(), local, Now));
    }

    [Fact]
    public void Create_rejects_invalid_fields()
    {
        Assert.Throws<ArgumentException>(() => TodoTask.Create(Guid.NewGuid(), Guid.NewGuid(), Fields(" "), CreatedOffline, Now));
    }

    [Fact]
    public void Update_increments_version_and_preserves_created_at()
    {
        var task = NewTask();
        var later = Now.AddMinutes(5);

        task.Update(Fields("Novo título") with { Status = TodoTaskStatus.Completed }, later);

        Assert.Equal(2, task.Version);
        Assert.Equal("Novo título", task.Title);
        Assert.Equal(TodoTaskStatus.Completed, task.Status);
        Assert.Equal(CreatedOffline, task.CreatedAt);
        Assert.Equal(later, task.UpdatedAt);
    }

    [Fact]
    public void Invalid_update_does_not_change_state()
    {
        var task = NewTask();

        Assert.Throws<ArgumentException>(() => task.Update(Fields(""), Now.AddMinutes(1)));
        Assert.Equal(1, task.Version);
        Assert.Equal("Conferir medição", task.Title);
    }

    [Fact]
    public void SoftDelete_sets_tombstone_and_increments_version()
    {
        var task = NewTask();
        var later = Now.AddMinutes(10);

        task.SoftDelete(later);

        Assert.True(task.IsDeleted);
        Assert.Equal(later, task.DeletedAt);
        Assert.Equal(later, task.UpdatedAt);
        Assert.Equal(2, task.Version);
    }

    [Fact]
    public void Deleted_task_cannot_be_changed_again()
    {
        var task = NewTask();
        task.SoftDelete(Now);

        Assert.Throws<InvalidOperationException>(() => task.Update(Fields(), Now));
        Assert.Throws<InvalidOperationException>(() => task.SoftDelete(Now));
        Assert.Equal(2, task.Version);
    }

    [Fact]
    public void Timestamps_are_truncated_to_milliseconds()
    {
        var withTicks = new DateTime(2026, 10, 28, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);

        var task = TodoTask.Create(Guid.NewGuid(), Guid.NewGuid(), Fields(), withTicks, withTicks);

        Assert.Equal(new DateTime(2026, 10, 28, 12, 0, 0, 123, DateTimeKind.Utc), task.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, task.UpdatedAt.Kind);
    }
}
