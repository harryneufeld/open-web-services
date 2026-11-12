namespace OpenWebServices.Domain.Admins;

/// <summary>A person allowed to sign in to the platform. Only a password hash is stored.</summary>
public sealed class AdminUser
{
    private AdminUser() { } // EF Core

    public Guid Id { get; private set; }
    public string Username { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }

    public static AdminUser Create(string username, string passwordHash, DateTime nowUtc)
    {
        var normalized = Normalize(username);
        if (normalized.Length is < 3 or > 64)
            throw new DomainException("Username must be 3-64 characters.");
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new DomainException("A password hash is required.");

        return new AdminUser
        {
            Id = Guid.NewGuid(),
            Username = normalized,
            PasswordHash = passwordHash,
            CreatedAtUtc = nowUtc,
        };
    }

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
