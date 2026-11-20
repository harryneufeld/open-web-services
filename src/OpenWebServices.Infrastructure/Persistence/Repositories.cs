using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.SshKeys;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Infrastructure.Persistence;

/// <summary>Saves and then tells open pages what changed, so they refresh without polling.</summary>
internal sealed class EfUnitOfWork(AppDbContext db, IChangeNotifier notifier) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        var changes = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => ToChange(e.Entity))
            .OfType<ResourceChange>()
            .Distinct()
            .ToList();

        await db.SaveChangesAsync(ct);

        foreach (var change in changes)
            notifier.Publish(change);
    }

    private static ResourceChange? ToChange(object entity) => entity switch
    {
        ProviderGroup g => new ResourceChange(ResourceTopics.Groups, g.Id),
        Server s => new ResourceChange(ResourceTopics.Servers, s.Id),
        DnsZone z => new ResourceChange(ResourceTopics.DnsZones, z.Id),
        AppInstall a => new ResourceChange(ResourceTopics.AppInstalls, a.ServerId),
        ServerKeyGrant g => new ResourceChange(ResourceTopics.Servers, g.ServerId),
        SshKey k => new ResourceChange(ResourceTopics.SshKeys, k.Id),
        _ => null,
    };
}

internal sealed class ProviderGroupRepository(AppDbContext db) : IProviderGroupRepository
{
    public void Add(ProviderGroup group) => db.ProviderGroups.Add(group);

    public void Remove(ProviderGroup group) => db.ProviderGroups.Remove(group);

    public Task<ProviderGroup?> FindAsync(Guid id, CancellationToken ct) =>
        db.ProviderGroups.FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<IReadOnlyList<ProviderGroup>> ListAsync(CancellationToken ct) =>
        await db.ProviderGroups.OrderBy(g => g.Name).ToListAsync(ct);
}

internal sealed class ServerRepository(AppDbContext db) : IServerRepository
{
    private static readonly ServerStatus[] Transitioning =
        [ServerStatus.Provisioning, ServerStatus.Starting, ServerStatus.Stopping, ServerStatus.Rescaling];

    public void Add(Server server) => db.Servers.Add(server);

    public void Remove(Server server) => db.Servers.Remove(server);

    public Task<Server?> FindAsync(Guid id, CancellationToken ct) =>
        db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct) =>
        await db.Servers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<Server>> ListByGroupAsync(Guid groupId, CancellationToken ct) =>
        await db.Servers.Where(s => s.GroupId == groupId).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListGroupsWithTransitioningServersAsync(CancellationToken ct) =>
        await db.Servers.Where(s => Transitioning.Contains(s.Status)).Select(s => s.GroupId).Distinct().ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListServersNeedingAccessCheckAsync(DateTime checkedBeforeUtc, CancellationToken ct) =>
        await db.Servers
            .Where(s => s.Status == ServerStatus.Running && s.PublicIpv4 != null && s.EncryptedSshPrivateKey != null)
            .Where(s => s.AccessCheckedAtUtc == null
                        || (s.RetiredAutomationPublicKey != null && s.AccessCheckedAtUtc < checkedBeforeUtc)
                        || (s.AccessError != null && s.AccessCheckedAtUtc < checkedBeforeUtc)
                        // A key was revoked since the last check: it may sit on this server without a grant.
                        || db.SshKeys.Any(k => k.TenantId == s.TenantId && k.RevokedAtUtc > s.AccessCheckedAtUtc))
            .Select(s => s.Id)
            .ToListAsync(ct);
}

internal sealed class DnsZoneRepository(AppDbContext db) : IDnsZoneRepository
{
    public void Add(DnsZone zone) => db.DnsZones.Add(zone);

    public void Remove(DnsZone zone) => db.DnsZones.Remove(zone);

    public Task<DnsZone?> FindAsync(Guid id, CancellationToken ct) =>
        db.DnsZones.FirstOrDefaultAsync(z => z.Id == id, ct);

    public async Task<IReadOnlyList<DnsZone>> ListAsync(CancellationToken ct) =>
        await db.DnsZones.AsNoTracking().OrderBy(z => z.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<DnsZone>> ListByGroupAsync(Guid groupId, CancellationToken ct) =>
        await db.DnsZones.Where(z => z.GroupId == groupId).ToListAsync(ct);
}

internal sealed class AppInstallRepository(AppDbContext db) : IAppInstallRepository
{
    public void Add(AppInstall install) => db.AppInstalls.Add(install);

    public Task<AppInstall?> FindAsync(Guid id, CancellationToken ct) =>
        db.AppInstalls.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<AppInstall>> ListForServerAsync(Guid serverId, CancellationToken ct) =>
        await db.AppInstalls.AsNoTracking()
            .Where(a => a.ServerId == serverId).OrderByDescending(a => a.CreatedAtUtc).ToListAsync(ct);

    public async Task<IReadOnlyList<AppInstall>> ListByStatusAsync(AppInstallStatus status, CancellationToken ct) =>
        await db.AppInstalls.Where(a => a.Status == status).OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
}

internal sealed class TenantRepository(AppDbContext db) : ITenantRepository
{
    public void Add(Tenant tenant) => db.Tenants.Add(tenant);

    public Task<Tenant?> FindAsync(Guid id, CancellationToken ct) => db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct) =>
        await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct) => db.Tenants.AnyAsync(ct);
}

internal sealed class TenantMembershipRepository(AppDbContext db) : ITenantMembershipRepository
{
    public void Add(TenantMembership membership) => db.TenantMemberships.Add(membership);

    public void Remove(TenantMembership membership) => db.TenantMemberships.Remove(membership);

    public Task<TenantMembership?> FindAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        db.TenantMemberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);

    public async Task<IReadOnlyList<TenantMembership>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
        await db.TenantMemberships.Where(m => m.TenantId == tenantId).ToListAsync(ct);

    public async Task<IReadOnlyList<TenantMembership>> ListByUserAsync(Guid userId, CancellationToken ct) =>
        await db.TenantMemberships.Where(m => m.UserId == userId).ToListAsync(ct);

    public async Task<IReadOnlyList<TenantMembership>> ListAllAsync(CancellationToken ct) =>
        await db.TenantMemberships.AsNoTracking().ToListAsync(ct);
}

internal sealed class TenantInvitationRepository(AppDbContext db) : ITenantInvitationRepository
{
    public void Add(TenantInvitation invitation) => db.TenantInvitations.Add(invitation);

    public Task<TenantInvitation?> FindAsync(Guid id, CancellationToken ct) =>
        db.TenantInvitations.FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<TenantInvitation?> FindByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        db.TenantInvitations.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<TenantInvitation>> ListOpenAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct) =>
        await db.TenantInvitations.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > nowUtc)
            .OrderByDescending(i => i.CreatedAtUtc)
            .ToListAsync(ct);
}

internal sealed class EfAuditLog(AppDbContext db, IClock clock) : IAuditLog
{
    public void Add(AuditEntry entry) => db.AuditEvents.Add(new AuditEvent
    {
        AtUtc = clock.UtcNow,
        Type = entry.Type,
        Detail = entry.Detail.Length > 1000 ? entry.Detail[..1000] : entry.Detail,
        ActorUserId = entry.ActorUserId,
        TenantId = entry.TenantId,
        IpAddress = entry.IpAddress,
    });

    public async Task<IReadOnlyList<AuditRecord>> ListRecentAsync(int count, CancellationToken ct) =>
        await db.AuditEvents.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(count)
            .Select(e => new AuditRecord(e.AtUtc, e.Type, e.Detail, e.ActorUserId, e.TenantId, e.IpAddress))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AuditRecord>> ListRecentAsync(
        Guid tenantId, IReadOnlyCollection<string> types, int count, CancellationToken ct) =>
        await db.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && types.Contains(e.Type))
            .OrderByDescending(e => e.Id)
            .Take(count)
            .Select(e => new AuditRecord(e.AtUtc, e.Type, e.Detail, e.ActorUserId, e.TenantId, e.IpAddress))
            .ToListAsync(ct);
}

internal sealed class SshKeyRepository(AppDbContext db) : ISshKeyRepository
{
    public void Add(SshKey key) => db.SshKeys.Add(key);

    public Task<SshKey?> FindAsync(Guid id, CancellationToken ct) => db.SshKeys.FirstOrDefaultAsync(k => k.Id == id, ct);

    public async Task<IReadOnlyList<SshKey>> ListAsync(CancellationToken ct) =>
        await db.SshKeys.OrderBy(k => k.CreatedAtUtc).ToListAsync(ct);

    public async Task<IReadOnlyList<SshKey>> ListByOwnerAsync(Guid tenantId, Guid ownerUserId, CancellationToken ct) =>
        await db.SshKeys.Where(k => k.TenantId == tenantId && k.OwnerUserId == ownerUserId).ToListAsync(ct);
}

internal sealed class ServerKeyGrantRepository(AppDbContext db) : IServerKeyGrantRepository
{
    public void Add(ServerKeyGrant grant) => db.ServerKeyGrants.Add(grant);

    public void Remove(ServerKeyGrant grant) => db.ServerKeyGrants.Remove(grant);

    public async Task<IReadOnlyList<ServerKeyGrant>> ListForServerAsync(Guid serverId, CancellationToken ct) =>
        await db.ServerKeyGrants.Where(g => g.ServerId == serverId).OrderBy(g => g.CreatedAtUtc).ToListAsync(ct);

    public async Task<IReadOnlyList<ServerKeyGrant>> ListForKeyAsync(Guid sshKeyId, CancellationToken ct) =>
        await db.ServerKeyGrants.Where(g => g.SshKeyId == sshKeyId).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListServersWithPendingChangesAsync(CancellationToken ct) =>
        await db.ServerKeyGrants.Where(g => g.AppliedAtUtc == null).Select(g => g.ServerId).Distinct().ToListAsync(ct);
}
