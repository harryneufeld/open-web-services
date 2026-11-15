namespace OpenWebServices.Domain.Tenants;

/// <summary>What a member may do in a tenant. Each role includes everything of the roles below it.</summary>
public enum TenantRole
{
    /// <summary>Sees resources, changes nothing.</summary>
    Viewer = 0,

    /// <summary>Operates resources: servers, DNS, apps.</summary>
    Member = 1,

    /// <summary>Also manages provider groups (credentials), domain orders, invitations and non-owner members.</summary>
    Admin = 2,

    /// <summary>Also manages owners and tenant settings.</summary>
    Owner = 3,
}

public static class TenantRoles
{
    public static IReadOnlyList<TenantRole> All { get; } = [TenantRole.Owner, TenantRole.Admin, TenantRole.Member, TenantRole.Viewer];

    public static string Describe(TenantRole role) => role switch
    {
        TenantRole.Owner => "Everything, including owners and tenant settings",
        TenantRole.Admin => "Provider groups, domain orders, invitations and members",
        TenantRole.Member => "Servers, DNS and apps",
        _ => "Read-only",
    };
}
