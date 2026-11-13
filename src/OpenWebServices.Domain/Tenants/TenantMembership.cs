namespace OpenWebServices.Domain.Tenants;

/// <summary>A user's access to one tenant.</summary>
public sealed class TenantMembership
{
    private TenantMembership() { } // EF Core

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public TenantRole Role { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static TenantMembership Create(Guid tenantId, Guid userId, TenantRole role, DateTime nowUtc)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            throw new DomainException("A tenant and a user are required.");
        return new TenantMembership { TenantId = tenantId, UserId = userId, Role = role, CreatedAtUtc = nowUtc };
    }

    public void ChangeRole(TenantRole role) => Role = role;
}
