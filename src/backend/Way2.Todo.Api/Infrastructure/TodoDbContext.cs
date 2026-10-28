using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Api.Infrastructure;

public sealed class TodoDbContext(DbContextOptions<TodoDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<TodoTask> Tasks => Set<TodoTask>();

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
    }

    private static ValueConverter<TEnum, string> LowercaseEnum<TEnum>() where TEnum : struct, Enum =>
        new(value => value.ToString().ToLowerInvariant(), value => Enum.Parse<TEnum>(value, true));
}
