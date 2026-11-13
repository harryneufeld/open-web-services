using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Infrastructure.Background;
using OpenWebServices.Infrastructure.Hetzner;
using OpenWebServices.Infrastructure.Identity;
using OpenWebServices.Infrastructure.Persistence;
using OpenWebServices.Infrastructure.Providers;
using OpenWebServices.Infrastructure.Security;
using OpenWebServices.Infrastructure.Ssh;
using OpenWebServices.Infrastructure.Templates;

namespace OpenWebServices.Infrastructure;

public static class DependencyInjection
{
    private const string HetznerBaseAddress = "https://api.hetzner.cloud/v1/";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
                               ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        AddDataProtection(services, configuration);
        AddIdentity(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISshKeyGenerator, RsaSshKeyGenerator>();
        services.AddSingleton<IRemoteShell, SshNetRemoteShell>();
        services.AddSingleton<IAppTemplateCatalog, BuiltInAppTemplateCatalog>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IChangeNotifier, InProcessChangeNotifier>();
        services.AddSingleton<SyncScheduler>();
        services.AddSingleton<ISyncScheduler>(sp => sp.GetRequiredService<SyncScheduler>());

        // Provider plug-ins: one IProviderConnector per provider type.
        services.AddHttpClient(HetznerConnector.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(HetznerBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IProviderConnector, HetznerConnector>();

        services.AddSingleton<ProviderCatalog>();
        services.AddSingleton<IProviderCatalog>(sp => sp.GetRequiredService<ProviderCatalog>());
        services.AddScoped<IProviderConnectionFactory, ProviderConnectionFactory>();

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IAuditLog, EfAuditLog>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<ITenantInvitationRepository, TenantInvitationRepository>();
        services.AddScoped<IProviderGroupRepository, ProviderGroupRepository>();
        services.AddScoped<IServerRepository, ServerRepository>();
        services.AddScoped<IDnsZoneRepository, DnsZoneRepository>();
        services.AddScoped<IAppInstallRepository, AppInstallRepository>();

        services.AddHostedService<GroupSyncWorker>();
        services.AddHostedService<AppInstallWorker>();
        return services;
    }

    /// <summary>
    /// Keys live in <c>DataProtection:KeysPath</c>. With <c>DataProtection:CertificatePath</c> (a PFX, password in
    /// <c>DataProtection:CertificatePassword</c>) the key ring itself is encrypted at rest, so a copy of the key
    /// directory alone cannot decrypt anything.
    /// </summary>
    private static void AddDataProtection(IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName("open-web-services")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["DataProtection:KeysPath"] ?? "data-protection-keys"));

        if (configuration["DataProtection:CertificatePath"] is { Length: > 0 } certificatePath)
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath, configuration["DataProtection:CertificatePassword"]);
            builder.ProtectKeysWithCertificate(certificate);
        }
    }

    private static void AddIdentity(IServiceCollection services)
    {
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = OwsPasswordHasher.Iterations);
        services.AddIdentityCore<OwsUser>(o =>
            {
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // passkeys
                o.User.RequireUniqueEmail = false;
                o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789-._@";
                // Length beats composition rules (NIST SP 800-63B).
                o.Password.RequiredLength = Passwords.MinLength;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 4;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddUserStore<OwsUserStore>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.Replace(ServiceDescriptor.Scoped<IPasswordHasher<OwsUser>, OwsPasswordHasher>());
    }

    /// <summary>Applies pending EF Core migrations and upgrades old secret formats. Call once at startup.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AccessContext>().BecomeSystem();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);

        var (upgraded, unreadable) = await LegacySecretUpgrade.RunAsync(db,
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(),
            scope.ServiceProvider.GetRequiredService<ISecretProtector>(),
            scope.ServiceProvider.GetRequiredService<IClock>(), ct);
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("OpenWebServices.Startup");
        if (upgraded > 0)
            logger.LogInformation("Re-encrypted {Count} secrets with tenant-bound keys.", upgraded);
        foreach (var item in unreadable)
            logger.LogWarning("Could not decrypt the {Secret} with the current key ring; enter it again.", item);
    }
}

public static class Passwords
{
    public const int MinLength = 12;
}
