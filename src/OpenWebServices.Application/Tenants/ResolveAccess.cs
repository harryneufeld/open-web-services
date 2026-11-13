using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Tenants;

/// <summary>
/// Fills the scope's <see cref="AccessContext"/> for a signed-in user from the database, so removed memberships,
/// changed roles and locked accounts take effect on the very next action, not only when the cookie is refreshed.
/// </summary>
public sealed class ResolveAccess(
    IUserDirectory users,
    ITenantMembershipRepository memberships,
    ITenantRepository tenants,
    AccessContext access)
{
    public async Task ExecuteAsync(Guid userId, bool strongSession, CancellationToken ct)
    {
        var user = await users.FindAsync(userId, ct);
        if (user is null || user.IsLockedOut)
            return; // stays without access

        access.SetUser(user.Id, user.UserName, strongSession, user.IsOwsAdmin);
        if (user.IsOwsAdmin && !strongSession)
        {
            access.Restrict(AccessRestriction.TwoFactorRequired);
            return;
        }

        var mine = await memberships.ListByUserAsync(userId, ct);
        var membership = mine.FirstOrDefault(m => m.TenantId == user.CurrentTenantId)
                         ?? mine.OrderBy(m => m.CreatedAtUtc).FirstOrDefault();
        if (membership is null || await tenants.FindAsync(membership.TenantId, ct) is not { } tenant)
            return;

        access.SetTenant(tenant.Id, tenant.Name, membership.Role);
        if (tenant.RequireTwoFactor && !strongSession)
            access.Restrict(AccessRestriction.TenantRequiresTwoFactor);
    }
}

public sealed record TenantSummary(Guid Id, string Name, TenantRole Role, bool RequireTwoFactor);

/// <summary>What the shell shows about the signed-in user: name, tenants, admin rights, restrictions.</summary>
public sealed record SessionView(
    Guid UserId,
    string UserName,
    bool IsOwsAdmin,
    bool IsOwsAdminAccount,
    bool HasSecondFactor,
    bool StrongSession,
    AccessRestriction Restriction,
    Guid? CurrentTenantId,
    IReadOnlyList<TenantSummary> Tenants)
{
    public TenantSummary? CurrentTenant => Tenants.FirstOrDefault(t => t.Id == CurrentTenantId);
}

public sealed class SessionQueries(
    AccessContext access,
    IUserDirectory users,
    ITenantMembershipRepository memberships,
    ITenantRepository tenants)
{
    /// <returns>Null when nobody (or a locked or deleted account) is signed in.</returns>
    public async Task<SessionView?> GetAsync(CancellationToken ct)
    {
        if (access.UserId is not { } userId || await users.FindAsync(userId, ct) is not { } user)
            return null;

        var all = (await tenants.ListAsync(ct)).ToDictionary(t => t.Id);
        var mine = (await memberships.ListByUserAsync(userId, ct))
            .Where(m => all.ContainsKey(m.TenantId))
            .Select(m => new TenantSummary(m.TenantId, all[m.TenantId].Name, m.Role, all[m.TenantId].RequireTwoFactor))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // While restricted, the selected tenant is still shown, so the shell can say what is locked and why.
        var current = access.TenantId ?? (access.Restriction != AccessRestriction.None
            ? (mine.FirstOrDefault(t => t.Id == user.CurrentTenantId) ?? mine.FirstOrDefault())?.Id
            : null);

        return new SessionView(user.Id, user.UserName, access.IsOwsAdmin, user.IsOwsAdmin, user.HasSecondFactor,
            access.StrongSession, access.Restriction, current, mine);
    }
}

/// <summary>Selects which of the user's tenants the console works in.</summary>
public sealed class SwitchTenant(AccessContext access, ITenantMembershipRepository memberships, IUserDirectory users)
{
    public async Task ExecuteAsync(Guid tenantId, CancellationToken ct)
    {
        var userId = access.RequireUser();
        if (await memberships.FindAsync(tenantId, userId, ct) is null)
            throw new NotFoundException("Tenant");
        await users.SetCurrentTenantAsync(userId, tenantId, ct);
    }
}
