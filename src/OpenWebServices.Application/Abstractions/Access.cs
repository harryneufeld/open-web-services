using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Abstractions;

/// <summary>Why a signed-in user currently has less access than their memberships would give.</summary>
public enum AccessRestriction
{
    None = 0,

    /// <summary>An ows admin without a second factor, or signed in without using it. Only account security works.</summary>
    TwoFactorRequired = 1,

    /// <summary>The selected tenant requires a second factor and this session did not use one.</summary>
    TenantRequiresTwoFactor = 2,
}

/// <summary>
/// Who is acting in the current DI scope. Filled once per scope (per use case call from the UI, per background job)
/// and read by use cases for permission checks and by persistence for tenant isolation. The default is no access:
/// a scope nobody filled sees no tenant data at all.
/// </summary>
public sealed class AccessContext
{
    /// <summary>Background work (sync, installs) that acts across tenants on behalf of the platform.</summary>
    public bool IsSystem { get; private set; }

    public Guid? UserId { get; private set; }
    public string UserName { get; private set; } = "";

    /// <summary>The session used a second factor (authenticator code or passkey).</summary>
    public bool StrongSession { get; private set; }

    /// <summary>The user is an ows admin and may use admin functions in this session.</summary>
    public bool IsOwsAdmin { get; private set; }

    public Guid? TenantId { get; private set; }
    public string TenantName { get; private set; } = "";
    public TenantRole? Role { get; private set; }
    public AccessRestriction Restriction { get; private set; }

    public void BecomeSystem()
    {
        IsSystem = true;
        UserId = null;
        TenantId = null;
        Role = null;
    }

    public void SetUser(Guid userId, string userName, bool strongSession, bool isOwsAdmin)
    {
        IsSystem = false;
        UserId = userId;
        UserName = userName;
        StrongSession = strongSession;
        IsOwsAdmin = isOwsAdmin;
    }

    public void SetTenant(Guid tenantId, string tenantName, TenantRole role)
    {
        TenantId = tenantId;
        TenantName = tenantName;
        Role = role;
    }

    public void Restrict(AccessRestriction restriction)
    {
        Restriction = restriction;
        TenantId = null;
        Role = null;
        if (restriction == AccessRestriction.TwoFactorRequired)
            IsOwsAdmin = false;
    }

    public bool Can(TenantRole minimum) => TenantId is not null && Role >= minimum;

    /// <summary>Returns the current tenant if the user holds at least <paramref name="minimum"/> in it.</summary>
    /// <exception cref="ForbiddenException">No tenant is selected or the role is too low.</exception>
    public Guid Require(TenantRole minimum) =>
        TenantId is { } id && Role >= minimum
            ? id
            : throw new ForbiddenException($"This needs the {minimum} role in the current tenant.");

    public Guid RequireUser() => UserId ?? throw new ForbiddenException("You need to be signed in.");

    public Guid RequireOwsAdmin() =>
        IsOwsAdmin && UserId is { } id ? id : throw new ForbiddenException("This needs an ows admin signed in with a second factor.");
}

public sealed class ForbiddenException(string message) : Exception(message);
