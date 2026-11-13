using Microsoft.AspNetCore.Identity;

namespace OpenWebServices.Infrastructure.Identity;

/// <summary>A sign-in account. Tenant access lives in tenant memberships, not on the user.</summary>
public sealed class OwsUser : IdentityUser<Guid>
{
    /// <summary>Platform administrator: manages tenants and accounts. Needs a second factor to use it.</summary>
    public bool IsOwsAdmin { get; set; }

    /// <summary>The tenant the console last worked in. Validated against memberships on every use.</summary>
    public Guid? CurrentTenantId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
