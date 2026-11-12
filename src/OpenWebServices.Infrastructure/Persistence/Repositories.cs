using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Admins;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Persistence;

internal sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

internal sealed class ServerRepository(AppDbContext db) : IServerRepository
{
    public void Add(Server server) => db.Servers.Add(server);

    public void Remove(Server server) => db.Servers.Remove(server);

    public Task<Server?> FindAsync(Guid id, CancellationToken ct) =>
        db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct) =>
        await db.Servers.AsNoTracking().OrderByDescending(s => s.CreatedAtUtc).ToListAsync(ct);

    public async Task<IReadOnlyList<Server>> ListByStatusAsync(ServerStatus status, CancellationToken ct) =>
        await db.Servers.Where(s => s.Status == status).ToListAsync(ct);
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

internal sealed class ProviderAccountRepository(AppDbContext db) : IProviderAccountRepository
{
    public void Add(ProviderAccount account) => db.ProviderAccounts.Add(account);

    public Task<ProviderAccount?> FindByProviderAsync(string providerId, CancellationToken ct) =>
        db.ProviderAccounts.FirstOrDefaultAsync(a => a.ProviderId == providerId, ct);
}
