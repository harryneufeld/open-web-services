using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Admins;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

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
        [ServerStatus.Provisioning, ServerStatus.Starting, ServerStatus.Stopping];

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

internal sealed class AdminUserRepository(AppDbContext db) : IAdminUserRepository
{
    public void Add(AdminUser user) => db.AdminUsers.Add(user);

    public Task<AdminUser?> FindByUsernameAsync(string normalizedUsername, CancellationToken ct) =>
        db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Username == normalizedUsername, ct);

    public Task<bool> AnyAsync(CancellationToken ct) => db.AdminUsers.AnyAsync(ct);
}
