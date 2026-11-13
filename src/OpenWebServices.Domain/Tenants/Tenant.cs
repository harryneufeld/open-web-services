namespace OpenWebServices.Domain.Tenants;

/// <summary>
/// A customer organization. Provider groups and every resource synced through them belong to exactly one tenant;
/// users get access through a <see cref="TenantMembership"/>.
/// </summary>
public sealed class Tenant
{
    public const int MaxNameLength = 100;

    private Tenant() { } // EF Core

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>Members must sign in with a second factor (authenticator code or passkey) to work in this tenant.</summary>
    public bool RequireTwoFactor { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static Tenant Create(string name, DateTime nowUtc)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), CreatedAtUtc = nowUtc };
        tenant.Rename(name);
        return tenant;
    }

    public void Rename(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
            throw new DomainException($"Tenant name must be 1-{MaxNameLength} characters.");
        Name = trimmed;
    }

    public void SetRequireTwoFactor(bool required) => RequireTwoFactor = required;
}
