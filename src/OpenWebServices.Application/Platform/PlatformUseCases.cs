using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Tenants;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Platform;

/// <summary>True while no account exists yet, i.e. the first-run setup page must be shown.</summary>
public sealed class IsSetupRequired(IUserDirectory users)
{
    public async Task<bool> ExecuteAsync(CancellationToken ct) => !await users.AnyAsync(ct);
}

/// <summary>Creates the first tenant for the account that ran setup and makes it the owner.</summary>
public sealed class CreateFirstTenant(
    ITenantRepository tenants,
    ITenantMembershipRepository memberships,
    IUserDirectory users,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(Guid userId, string tenantName, string? ipAddress, CancellationToken ct)
    {
        if (await tenants.AnyAsync(ct))
            throw new DomainException("Setup has already been completed.");

        var tenant = Tenant.Create(tenantName, clock.UtcNow);
        tenants.Add(tenant);
        memberships.Add(TenantMembership.Create(tenant.Id, userId, TenantRole.Owner, clock.UtcNow));
        audit.Add(new AuditEntry(AuditTypes.Setup, $"Created tenant '{tenant.Name}'", userId, tenant.Id, ipAddress));
        await unitOfWork.SaveChangesAsync(ct);
        await users.SetCurrentTenantAsync(userId, tenant.Id, ct);
    }
}

public sealed record TenantOverview(Tenant Tenant, int MemberCount, IReadOnlyList<string> Owners);

public sealed record UserOverview(UserSummary User, IReadOnlyList<string> Tenants);

public sealed record AuditView(AuditRecord Record, string Actor, string Tenant);

/// <summary>Platform-wide views for ows admins.</summary>
public sealed class PlatformQueries(
    AccessContext access,
    ITenantRepository tenants,
    ITenantMembershipRepository memberships,
    IUserDirectory users,
    IAuditLog audit)
{
    public async Task<IReadOnlyList<TenantOverview>> ListTenantsAsync(CancellationToken ct)
    {
        access.RequireOwsAdmin();
        var all = await memberships.ListAllAsync(ct);
        var names = (await users.ListAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        return [.. (await tenants.ListAsync(ct))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TenantOverview(t,
                all.Count(m => m.TenantId == t.Id),
                [.. all.Where(m => m.TenantId == t.Id && m.Role == TenantRole.Owner).Select(m => names.GetValueOrDefault(m.UserId, "?"))]))];
    }

    public async Task<IReadOnlyList<UserOverview>> ListUsersAsync(CancellationToken ct)
    {
        access.RequireOwsAdmin();
        var tenantNames = (await tenants.ListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        var all = await memberships.ListAllAsync(ct);
        return [.. (await users.ListAsync(ct))
            .OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(u => new UserOverview(u,
                [.. all.Where(m => m.UserId == u.Id).Select(m => $"{tenantNames.GetValueOrDefault(m.TenantId, "?")} ({m.Role})")]))];
    }

    public async Task<IReadOnlyList<AuditView>> ListAuditAsync(int count, CancellationToken ct)
    {
        access.RequireOwsAdmin();
        var names = (await users.ListAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        var tenantNames = (await tenants.ListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        return [.. (await audit.ListRecentAsync(count, ct)).Select(r => new AuditView(r,
            r.ActorUserId is { } a ? names.GetValueOrDefault(a, "deleted user") : "",
            r.TenantId is { } t ? tenantNames.GetValueOrDefault(t, "deleted tenant") : ""))];
    }
}

/// <summary>Creates a tenant. Its first owner is either the admin or whoever opens the returned invitation link.</summary>
public sealed class CreateTenant(
    AccessContext access,
    ITenantRepository tenants,
    ITenantMembershipRepository memberships,
    ITenantInvitationRepository invitations,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <returns>The owner invitation token, or null when the admin became the owner.</returns>
    public async Task<string?> ExecuteAsync(string name, bool addMeAsOwner, string ownerNote, CancellationToken ct)
    {
        var adminId = access.RequireOwsAdmin();
        var tenant = Tenant.Create(name, clock.UtcNow);
        tenants.Add(tenant);
        audit.Add(new AuditEntry(AuditTypes.TenantCreated, $"Tenant '{tenant.Name}'", adminId, tenant.Id));

        string? token = null;
        if (addMeAsOwner)
        {
            memberships.Add(TenantMembership.Create(tenant.Id, adminId, TenantRole.Owner, clock.UtcNow));
        }
        else
        {
            (token, var hash) = InvitationTokens.Generate();
            invitations.Add(TenantInvitation.Create(tenant.Id, TenantRole.Owner, ownerNote, hash, adminId, clock.UtcNow));
            audit.Add(new AuditEntry(AuditTypes.InvitationCreated, $"Owner invitation{CreateInvitation.NoteSuffix(ownerNote)}", adminId, tenant.Id));
        }

        await unitOfWork.SaveChangesAsync(ct);
        return token;
    }
}

/// <summary>Lets an admin hand a tenant a new owner, e.g. when its only owner left.</summary>
public sealed class CreateOwnerInvitation(
    AccessContext access, ITenantRepository tenants, ITenantInvitationRepository invitations,
    IAuditLog audit, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task<string> ExecuteAsync(Guid tenantId, string note, CancellationToken ct)
    {
        var adminId = access.RequireOwsAdmin();
        _ = await tenants.FindAsync(tenantId, ct) ?? throw new NotFoundException("Tenant");
        var (token, hash) = InvitationTokens.Generate();
        invitations.Add(TenantInvitation.Create(tenantId, TenantRole.Owner, note, hash, adminId, clock.UtcNow));
        audit.Add(new AuditEntry(AuditTypes.InvitationCreated, $"Owner invitation{CreateInvitation.NoteSuffix(note)}", adminId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
        return token;
    }
}

public sealed class SetOwsAdmin(AccessContext access, IUserDirectory users, IAuditLog audit, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid userId, bool isOwsAdmin, CancellationToken ct)
    {
        var adminId = access.RequireOwsAdmin();
        var all = await users.ListAsync(ct);
        var user = all.FirstOrDefault(u => u.Id == userId) ?? throw new NotFoundException("User");
        if (!isOwsAdmin && !all.Any(u => u.IsOwsAdmin && u.Id != userId && !u.IsLockedOut))
            throw new DomainException("ows needs at least one active admin.");

        await users.SetOwsAdminAsync(userId, isOwsAdmin, ct);
        audit.Add(new AuditEntry(AuditTypes.OwsAdminChanged, $"{user.UserName}: {(isOwsAdmin ? "granted" : "revoked")}", adminId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}

public sealed class SetUserLocked(AccessContext access, IUserDirectory users, IAuditLog audit, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid userId, bool locked, CancellationToken ct)
    {
        var adminId = access.RequireOwsAdmin();
        if (userId == adminId)
            throw new DomainException("You cannot lock your own account.");
        var user = await users.FindAsync(userId, ct) ?? throw new NotFoundException("User");

        await users.SetLockedAsync(userId, locked, ct);
        audit.Add(new AuditEntry(locked ? AuditTypes.UserLocked : AuditTypes.UserUnlocked, user.UserName, adminId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}

public sealed class ResetUserSecondFactors(AccessContext access, IUserDirectory users, IAuditLog audit, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid userId, CancellationToken ct)
    {
        var adminId = access.RequireOwsAdmin();
        if (userId == adminId)
            throw new DomainException("Manage your own second factors under Account security.");
        var user = await users.FindAsync(userId, ct) ?? throw new NotFoundException("User");

        await users.ResetSecondFactorsAsync(userId, ct);
        audit.Add(new AuditEntry(AuditTypes.SecondFactorsReset, user.UserName, adminId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}
