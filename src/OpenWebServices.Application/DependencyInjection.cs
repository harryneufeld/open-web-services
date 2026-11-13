using Microsoft.Extensions.DependencyInjection;
using OpenWebServices.Application.Admins;
using OpenWebServices.Application.Apps;
using OpenWebServices.Application.Dns;
using OpenWebServices.Application.Groups;
using OpenWebServices.Application.Offers;
using OpenWebServices.Application.Servers;
using OpenWebServices.Application.Sync;

namespace OpenWebServices.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<AddProviderGroup>()
        .AddScoped<UpdateProviderGroup>()
        .AddScoped<RemoveProviderGroup>()
        .AddScoped<GroupQueries>()
        .AddScoped<SyncGroup>()
        .AddScoped<IResourceSync, ServerInventorySync>()
        .AddScoped<IResourceSync, DnsZoneInventorySync>()
        .AddScoped<ProvisionServer>()
        .AddScoped<ControlServerPower>()
        .AddScoped<DeleteServer>()
        .AddScoped<ServerQueries>()
        .AddScoped<GetServerOffers>()
        .AddScoped<DnsQueries>()
        .AddScoped<CreateDnsZone>()
        .AddScoped<DeleteDnsZone>()
        .AddScoped<DnsRecords>()
        .AddScoped<RequestAppInstall>()
        .AddScoped<RunAppInstall>()
        .AddScoped<IsSetupRequired>()
        .AddScoped<RegisterFirstAdmin>()
        .AddScoped<AuthenticateAdmin>();
}
