using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>
/// Addresses of OVHcloud Public Cloud instances, read from the instance itself. Reverse DNS and private networks
/// (vRack) need further API routes and stay in the OVHcloud Control Panel for now.
/// </summary>
internal sealed class OvhNetworkProvider(OvhApiClient api, string projectId) : IServerNetworkProvider
{
    private const string NotManaged = "Manage reverse DNS and private networks of OVHcloud instances in the OVHcloud Control Panel.";

    public async Task<ServerNetworking> GetServerNetworkingAsync(string externalServerId, CancellationToken ct)
    {
        var instance = await api.GetAsync<OvhInstance>($"cloud/project/{Escape(projectId)}/instance/{Escape(externalServerId)}", ct)
                       ?? throw new NotFoundException("Server at OVHcloud");
        var addresses = instance.IpAddresses ?? [];
        var publicIps = addresses.Where(ip => ip.Type == "public").ToList();
        return new ServerNetworking(
            publicIps.FirstOrDefault(ip => ip.Version == 4)?.Ip,
            publicIps.FirstOrDefault(ip => ip.Version == 6)?.Ip,
            [.. addresses.Where(ip => ip.Type == "private")
                .Select(ip => new PrivateNetworkAttachment(ip.NetworkId ?? "", ip.NetworkId ?? "Private network", ip.Ip))],
            [],
            CanEditReverseDns: false,
            CanManagePrivateNetworks: false);
    }

    public Task SetReverseDnsAsync(string externalServerId, string ip, string? ptr, CancellationToken ct) =>
        throw new ProviderException(NotManaged);

    public Task<IReadOnlyList<PrivateNetwork>> ListPrivateNetworksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PrivateNetwork>>([]);

    public Task<PrivateNetwork> CreatePrivateNetworkAsync(string name, string ipRange, CancellationToken ct) =>
        throw new ProviderException(NotManaged);

    public Task AttachToPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct) =>
        throw new ProviderException(NotManaged);

    public Task DetachFromPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct) =>
        throw new ProviderException(NotManaged);
}
