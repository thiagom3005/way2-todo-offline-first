namespace Way2.Todo.Api.Identity;

public sealed class User
{
    private User() { } // EF Core

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static User Create(string email, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        Email = NormalizeEmail(email),
        CreatedAt = now,
    };

    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;
}
