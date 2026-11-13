using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;
using OpenWebServices.Infrastructure.Identity;

namespace OpenWebServices.Infrastructure.Persistence;

/// <summary>
/// Tenant-owned data (groups, servers, zones, installs) is filtered by the scope's <see cref="AccessContext"/>:
/// a query only ever sees the current tenant's rows, or none when no tenant is selected. Only background work
/// running as system sees all tenants.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, AccessContext access)
    : IdentityUserContext<OwsUser, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<TenantInvitation> TenantInvitations => Set<TenantInvitation>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ProviderGroup> ProviderGroups => Set<ProviderGroup>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<DnsZone> DnsZones => Set<DnsZone>();
    public DbSet<AppInstall> AppInstalls => Set<AppInstall>();

    // Read by the query filters; EF evaluates them per query, not once per model.
    private bool SeesAllTenants => access.IsSystem;
    private Guid? CurrentTenantId => access.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<OwsUser>().ToTable("users");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        modelBuilder.Entity<IdentityUserPasskey<Guid>>().ToTable("user_passkeys");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<ProviderGroup>().HasQueryFilter(e => SeesAllTenants || e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Server>().HasQueryFilter(e => SeesAllTenants || e.TenantId == CurrentTenantId);
        modelBuilder.Entity<DnsZone>().HasQueryFilter(e => SeesAllTenants || e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AppInstall>().HasQueryFilter(e => SeesAllTenants || e.TenantId == CurrentTenantId);
    }
}

/// <summary>One row of the security audit log. Never updated or deleted by the application.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
    public Guid? ActorUserId { get; set; }
    public Guid? TenantId { get; set; }
    public string? IpAddress { get; set; }
}
