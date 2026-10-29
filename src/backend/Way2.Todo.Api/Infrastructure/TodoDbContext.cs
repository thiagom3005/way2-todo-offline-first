using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Integrations;
using Way2.Todo.Api.Sync;
using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Api.Infrastructure;

public sealed class TodoDbContext(DbContextOptions<TodoDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<TodoTask> Tasks => Set<TodoTask>();
    public DbSet<UserSyncCounter> SyncCounters => Set<UserSyncCounter>();
    public DbSet<ProcessedOperation> ProcessedOperations => Set<ProcessedOperation>();
    public DbSet<TaskChangeLogEntry> TaskChangeLog => Set<TaskChangeLogEntry>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    /// <summary>Configuração única usada pela API, pelas migrations e pelos testes.</summary>
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.Email).HasMaxLength(254).IsRequired();
            user.Property(u => u.PasswordHash).IsRequired();
            user.HasIndex(u => u.Email).IsUnique();
        });

        model.Entity<TodoTask>(task =>
        {
            task.HasKey(t => t.Id);
            task.Property(t => t.Id).ValueGeneratedNever();
            task.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);

            task.Property(t => t.Title).HasMaxLength(TaskFields.TitleMaxLength).IsRequired();
            task.Property(t => t.Description).HasMaxLength(TaskFields.DescriptionMaxLength);
            task.Property(t => t.Category).HasMaxLength(TaskFields.CategoryMaxLength).IsRequired();
            task.Property(t => t.Priority).HasConversion(LowercaseEnum<TaskPriority>()).HasMaxLength(16);
            task.Property(t => t.Status).HasConversion(LowercaseEnum<TodoTaskStatus>()).HasMaxLength(16);

            // Proteção extra contra atualizações concorrentes: UPDATE ... WHERE version = <lida>.
            task.Property(t => t.Version).IsConcurrencyToken();
            task.Ignore(t => t.IsDeleted);

            task.HasIndex(t => new { t.UserId, t.DeletedAt });
            task.HasIndex(t => new { t.UserId, t.Category });

            task.ToTable(t =>
            {
                t.HasCheckConstraint("ck_tasks_priority", "priority IN ('low', 'medium', 'high')");
                t.HasCheckConstraint("ck_tasks_status", "status IN ('pending', 'completed')");
                t.HasCheckConstraint("ck_tasks_version", "version >= 1");
            });
        });

        model.Entity<UserSyncCounter>(counter =>
        {
            counter.ToTable("user_sync_counters");
            counter.HasKey(c => c.UserId);
            counter.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ProcessedOperation>(operation =>
        {
            // PK (user_id, operation_id): idempotência garantida pelo banco, não só pela checagem prévia.
            operation.ToTable("processed_operations");
            operation.HasKey(p => new { p.UserId, p.OperationId });
            operation.Property(p => p.RequestHash).HasMaxLength(64).IsFixedLength();
            operation.Property(p => p.ResultJson).HasColumnType("jsonb");
            operation.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<TaskChangeLogEntry>(change =>
        {
            // PK (user_id, revision): cursor monotônico por usuário e índice do pull.
            change.ToTable("task_change_log", t =>
                t.HasCheckConstraint("ck_task_change_log_change_type", "change_type IN ('upsert', 'delete')"));
            change.HasKey(c => new { c.UserId, c.Revision });
            change.Property(c => c.ChangeType).HasMaxLength(16);
            change.Property(c => c.SnapshotJson).HasColumnType("jsonb");
            change.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<OutboxEvent>(outbox =>
        {
            outbox.ToTable("integration_outbox", t =>
            {
                t.HasCheckConstraint(
                    "ck_integration_outbox_event_type",
                    "event_type IN ('task.created', 'task.updated', 'task.deleted')");
                t.HasCheckConstraint("ck_integration_outbox_status", "status IN ('pending', 'delivered')");
            });
            outbox.HasKey(e => e.EventId);
            outbox.Property(e => e.EventId).ValueGeneratedNever();
            outbox.Property(e => e.EventType).HasMaxLength(32);
            outbox.Property(e => e.Status).HasMaxLength(16);
            outbox.Property(e => e.PayloadJson).HasColumnType("jsonb");
            outbox.Property(e => e.LastError).HasMaxLength(300);
            outbox.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);

            // Um evento por mudança aplicada (a revisão é única por usuário).
            outbox.HasIndex(e => new { e.UserId, e.ServerRevision }).IsUnique();
            // Varredura do dispatcher e checagem "há evento anterior pendente desta tarefa?".
            outbox.HasIndex(e => new { e.Status, e.NextRetryAt });
            outbox.HasIndex(e => new { e.TaskId, e.ServerRevision }).HasFilter("status = 'pending'");
        });
    }

    private static ValueConverter<TEnum, string> LowercaseEnum<TEnum>() where TEnum : struct, Enum =>
        new(value => value.ToString().ToLowerInvariant(), value => Enum.Parse<TEnum>(value, true));
}
