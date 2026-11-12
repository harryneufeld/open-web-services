using Microsoft.Extensions.DependencyInjection;
using OpenWebServices.Application.Admins;
using OpenWebServices.Application.Apps;
using OpenWebServices.Application.Offers;
using OpenWebServices.Application.Providers;
using OpenWebServices.Application.Servers;

namespace OpenWebServices.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<ProvisionServer>()
        .AddScoped<SyncServer>()
        .AddScoped<ControlServerPower>()
        .AddScoped<DeleteServer>()
        .AddScoped<ServerQueries>()
        .AddScoped<GetServerOffers>()
        .AddScoped<RequestAppInstall>()
        .AddScoped<RunAppInstall>()
        .AddScoped<SaveProviderToken>()
        .AddScoped<IsSetupRequired>()
        .AddScoped<RegisterFirstAdmin>()
        .AddScoped<AuthenticateAdmin>();
}
