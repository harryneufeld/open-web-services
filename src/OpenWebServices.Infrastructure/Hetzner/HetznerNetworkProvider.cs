using System.Globalization;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Servers;
using static OpenWebServices.Infrastructure.Hetzner.HetznerServerProvider;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>Public addresses, reverse DNS and private networks of Hetzner Cloud servers.</summary>
internal sealed class HetznerNetworkProvider(HetznerApiClient api) : IServerNetworkProvider
{
    private const string CloudSubnet = "cloud";

    public async Task<ServerNetworking> GetServerNetworkingAsync(string externalServerId, CancellationToken ct)
    {
        var server = await GetServerAsync(externalServerId, ct);
        var networkNames = server.PrivateNet is { Count: > 0 }
            ? (await api.GetAllAsync<HetznerNetwork>("networks", "networks", ct)).ToDictionary(n => n.Id, n => n.Name)
            : [];

        var ipv4 = server.PublicNet?.Ipv4;
        var ipv6 = server.PublicNet?.Ipv6;
        var reverse = new List<ReverseDnsEntry>();
        if (ipv4 is not null)
            reverse.Add(new ReverseDnsEntry(ipv4.Ip, ipv4.DnsPtr));
        reverse.AddRange((ipv6?.DnsPtr ?? []).Select(p => new ReverseDnsEntry(p.Ip, p.DnsPtr, IsRemovable: true)));

        return new ServerNetworking(
            ipv4?.Ip,
            ipv6?.Ip,
            [.. (server.PrivateNet ?? []).Select(p => new PrivateNetworkAttachment(
                Id(p.Network), networkNames.GetValueOrDefault(p.Network) ?? Id(p.Network), p.Ip))],
            reverse,
            CanEditReverseDns: true,
            CanManagePrivateNetworks: true);
    }

    public async Task SetReverseDnsAsync(string externalServerId, string ip, string? ptr, CancellationToken ct)
    {
        var envelope = await api.PostAsync<HetznerActionEnvelope>(
            $"servers/{ParseId(externalServerId)}/actions/change_dns_ptr", new HetznerChangeDnsPtrBody(ip, ptr), ct, includeNulls: true);
        await api.WaitAsync(envelope.Action, ct);
    }

    public async Task<IReadOnlyList<PrivateNetwork>> ListPrivateNetworksAsync(CancellationToken ct) =>
        [.. (await api.GetAllAsync<HetznerNetwork>("networks", "networks", ct)).Select(ToNetwork)];

    public async Task<PrivateNetwork> CreatePrivateNetworkAsync(string name, string ipRange, string forServerId, CancellationToken ct)
    {
        var created = await api.PostAsync<HetznerNetworkEnvelope>(
            "networks", new HetznerCreateNetworkBody(name, ipRange, new() { ["managed-by"] = "ows" }), ct);
        return ToNetwork(created.Network);
    }

    public async Task AttachToPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct)
    {
        var server = await GetServerAsync(externalServerId, ct);
        var zone = server.Location?.NetworkZone
                   ?? throw new ProviderException("Hetzner did not report the server's network zone.");
        var networkPath = $"networks/{ParseId(networkId)}";
        var network = (await api.GetAsync<HetznerNetworkEnvelope>(networkPath, ct))?.Network
                      ?? throw new NotFoundException("Private network");

        // A server can only join a network that has a subnet in its own zone (e.g. eu-central).
        var subnets = network.Subnets ?? [];
        if (!subnets.Any(s => s.Type == CloudSubnet && s.NetworkZone == zone))
        {
            var range = IpRanges.NextFreeSubnet(network.IpRange, [.. subnets.Select(s => s.IpRange).OfType<string>()])
                        ?? throw new ProviderException($"The network {network.Name} has no free /24 left for a subnet in {zone}.");
            var added = await api.PostAsync<HetznerActionEnvelope>(
                $"{networkPath}/actions/add_subnet", new HetznerAddSubnetBody(CloudSubnet, zone, range), ct);
            await api.WaitAsync(added.Action, ct);
        }

        var attached = await api.PostAsync<HetznerActionEnvelope>(
            $"servers/{server.Id}/actions/attach_to_network", new HetznerNetworkRefBody(network.Id), ct);
        await api.WaitAsync(attached.Action, ct);
    }

    public async Task DetachFromPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct)
    {
        var detached = await api.PostAsync<HetznerActionEnvelope>(
            $"servers/{ParseId(externalServerId)}/actions/detach_from_network", new HetznerNetworkRefBody(ParseId(networkId)), ct);
        await api.WaitAsync(detached.Action, ct);
    }

    private async Task<HetznerServer> GetServerAsync(string externalServerId, CancellationToken ct) =>
        (await api.GetAsync<HetznerServerEnvelope>($"servers/{ParseId(externalServerId)}", ct))?.Server
        ?? throw new NotFoundException("Server at Hetzner");

    private static PrivateNetwork ToNetwork(HetznerNetwork network) =>
        new(Id(network.Id), network.Name, network.IpRange, [.. (network.Servers ?? []).Select(Id)]);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
