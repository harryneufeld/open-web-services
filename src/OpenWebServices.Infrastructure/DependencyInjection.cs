using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Infrastructure.Background;
using OpenWebServices.Infrastructure.Hetzner;
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
        var keysPath = configuration["DataProtection:KeysPath"] ?? "data-protection-keys";

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddDataProtection()
            .SetApplicationName("open-web-services")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISshKeyGenerator, RsaSshKeyGenerator>();
        services.AddSingleton<IRemoteShell, SshNetRemoteShell>();
        services.AddSingleton<IAppTemplateCatalog, BuiltInAppTemplateCatalog>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
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
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();
        services.AddScoped<IProviderGroupRepository, ProviderGroupRepository>();
        services.AddScoped<IServerRepository, ServerRepository>();
        services.AddScoped<IDnsZoneRepository, DnsZoneRepository>();
        services.AddScoped<IAppInstallRepository, AppInstallRepository>();

        services.AddHostedService<GroupSyncWorker>();
        services.AddHostedService<AppInstallWorker>();
        return services;
    }

    /// <summary>Applies pending EF Core migrations. Call once at startup.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
    }
}
