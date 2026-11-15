using Microsoft.Extensions.DependencyInjection;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Apps;
using OpenWebServices.Application.Dns;
using OpenWebServices.Application.Domains;
using OpenWebServices.Application.Groups;
using OpenWebServices.Application.Offers;
using OpenWebServices.Application.Platform;
using OpenWebServices.Application.Servers;
using OpenWebServices.Application.Sync;
using OpenWebServices.Application.Tenants;

namespace OpenWebServices.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<AccessContext>()
        .AddScoped<ResolveAccess>()
        .AddScoped<SessionQueries>()
        .AddScoped<SwitchTenant>()
        .AddScoped<TenantQueries>()
        .AddScoped<UpdateTenantSettings>()
        .AddScoped<CreateInvitation>()
        .AddScoped<RevokeInvitation>()
        .AddScoped<InvitationQueries>()
        .AddScoped<AcceptInvitation>()
        .AddScoped<ChangeMemberRole>()
        .AddScoped<RemoveMember>()
        .AddScoped<IsSetupRequired>()
        .AddScoped<CreateFirstTenant>()
        .AddScoped<PlatformQueries>()
        .AddScoped<CreateTenant>()
        .AddScoped<CreateOwnerInvitation>()
        .AddScoped<SetOwsAdmin>()
        .AddScoped<SetUserLocked>()
        .AddScoped<ResetUserSecondFactors>()
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
        .AddScoped<ServerNetworks>()
        .AddScoped<ServerFirewalls>()
        .AddScoped<ServerLabels>()
        .AddScoped<GetServerOffers>()
        .AddScoped<DnsQueries>()
        .AddScoped<CreateDnsZone>()
        .AddScoped<DeleteDnsZone>()
        .AddScoped<DnsRecords>()
        .AddScoped<DomainSearch>()
        .AddScoped<DomainQueries>()
        .AddScoped<QuoteDomainOrder>()
        .AddScoped<PlaceDomainOrder>()
        .AddScoped<ChangeDomainCancellation>()
        .AddScoped<QuoteDomainTransfer>()
        .AddScoped<ChangeDomainTransferLock>()
        .AddScoped<RevealDomainAuthCode>()
        .AddScoped<RequestAppInstall>()
        .AddScoped<RunAppInstall>();
}
