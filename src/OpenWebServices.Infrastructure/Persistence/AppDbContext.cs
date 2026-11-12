using Microsoft.EntityFrameworkCore;
using OpenWebServices.Domain.Admins;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<AppInstall> AppInstalls => Set<AppInstall>();
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
