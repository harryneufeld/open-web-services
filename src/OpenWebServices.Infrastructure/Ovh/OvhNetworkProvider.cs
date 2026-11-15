using System.Net;
using System.Net.Sockets;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Servers;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>
/// Addresses, reverse DNS and private networks of OVHcloud Public Cloud instances.
/// Reverse DNS is set on the instance's addresses under /ip. Private networks are the project's vRack networks
/// (/cloud/project/{id}/network/private): a network is activated per region, gets one subnet pool per region, and an
/// instance joins it through a network interface. Without a vRack on the project, OVHcloud refuses to create networks.
/// </summary>
internal sealed class OvhNetworkProvider(OvhApiClient api, string projectId) : IServerNetworkProvider
{
    private const string Active = "ACTIVE";
    private static readonly HttpStatusCode[] Missing = [HttpStatusCode.NotFound];

    /// <summary>How often network status is polled while OVHcloud builds it; tests set it to zero.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(90);

    private string ProjectPath => $"cloud/project/{Escape(projectId)}";

    public async Task<ServerNetworking> GetServerNetworkingAsync(string externalServerId, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(externalServerId, ct);
        var addresses = instance.IpAddresses ?? [];
        var publicIps = addresses.Where(ip => ip.Type == "public").ToList();
        var ipv4 = publicIps.FirstOrDefault(ip => ip.Version == 4)?.Ip;
        var ipv6 = publicIps.FirstOrDefault(ip => ip.Version == 6)?.Ip;

        var reverse = new List<ReverseDnsEntry>();
        foreach (var ip in new[] { ipv4, ipv6 }.OfType<string>())
            reverse.Add(new ReverseDnsEntry(ip, await GetReverseAsync(ip, ct), IsRemovable: true));

        var privateIps = addresses.Where(ip => ip.Type == "private").ToList();
        var networks = privateIps.Count > 0 ? await ListNetworksAsync(ct) : [];
        var attachments = privateIps.Select(ip =>
        {
            var network = networks.FirstOrDefault(n => n.Regions?.Any(r => r.OpenstackId == ip.NetworkId) == true);
            return new PrivateNetworkAttachment(network?.Id ?? ip.NetworkId ?? "", network?.Name ?? "Private network", ip.Ip);
        }).ToList();

        return new ServerNetworking(ipv4, ipv6, attachments, reverse, CanEditReverseDns: true, CanManagePrivateNetworks: true);
    }

    public async Task SetReverseDnsAsync(string externalServerId, string ip, string? ptr, CancellationToken ct)
    {
        var path = $"ip/{Escape(Block(ip))}/reverse";
        if (ptr is null)
            await api.DeleteAsync($"{path}/{Escape(ip)}", ct);
        else
            await api.PostAsync(path, new OvhSetReverse(ip, ptr.EndsWith('.') ? ptr : ptr + "."), ct);
    }

    public async Task<IReadOnlyList<PrivateNetwork>> ListPrivateNetworksAsync(CancellationToken ct)
    {
        var networks = await ListNetworksAsync(ct);
        var result = new List<PrivateNetwork>();
        foreach (var network in networks)
        {
            var subnets = await api.GetAllAsync<OvhSubnet>($"{NetworkPath(network.Id)}/subnet", ct);
            result.Add(new PrivateNetwork(network.Id, network.Name, subnets.Select(s => s.Cidr).FirstOrDefault(c => c is not null) ?? "", []));
        }

        return result;
    }

    public async Task<PrivateNetwork> CreatePrivateNetworkAsync(string name, string ipRange, string forServerId, CancellationToken ct)
    {
        var region = (await GetInstanceAsync(forServerId, ct)).Region
                     ?? throw new ProviderException("OVHcloud did not report the server's region.");
        var created = await api.PostAsync<OvhPrivateNetwork>(
            $"{ProjectPath}/network/private", new OvhCreatePrivateNetwork(name, [region]), ct);
        await WaitUntilActiveAsync(created.Id, region, ct);
        await AddSubnetAsync(created.Id, ipRange, region, [], ct);
        return new PrivateNetwork(created.Id, created.Name, ipRange, []);
    }

    public async Task AttachToPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct)
    {
        var region = (await GetInstanceAsync(externalServerId, ct)).Region
                     ?? throw new ProviderException("OVHcloud did not report the server's region.");
        var network = await GetNetworkAsync(networkId, ct);
        if (network.Regions?.Any(r => r.Region == region) != true)
        {
            await api.PostAsync($"{NetworkPath(networkId)}/region", new OvhAddNetworkRegion(region), ct);
            await WaitUntilActiveAsync(networkId, region, ct);
            network = await GetNetworkAsync(networkId, ct);
        }

        // Each region of the network needs its own address pool before an instance there can join.
        var subnets = await api.GetAllAsync<OvhSubnet>($"{NetworkPath(networkId)}/subnet", ct);
        var pools = subnets.SelectMany(s => s.IpPools ?? []).ToList();
        if (!pools.Any(p => p.Region == region))
        {
            var range = subnets.Select(s => s.Cidr).FirstOrDefault(c => c is not null)
                        ?? throw new ProviderException(
                            $"The network {network.Name} has no subnet yet. Add one in the OVHcloud Control Panel, then join again.");
            await AddSubnetAsync(networkId, range, region, pools, ct);
        }

        var openstackId = OpenstackId(network, region);
        await api.PostAsync($"{InstancePath(externalServerId)}/interface", new OvhCreateInterface(openstackId), ct);
    }

    public async Task DetachFromPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(externalServerId, ct);
        var network = await GetNetworkAsync(networkId, ct);
        var openstackIds = (network.Regions ?? []).Select(r => r.OpenstackId).OfType<string>().Append(network.Id).ToHashSet();
        var interfaces = await api.GetAllAsync<OvhInterface>($"{InstancePath(instance.Id)}/interface", ct);
        var attached = interfaces.FirstOrDefault(i => openstackIds.Contains(i.NetworkId))
                       ?? throw new ProviderException($"The server is not attached to {network.Name}.");
        await api.DeleteAsync($"{InstancePath(instance.Id)}/interface/{Escape(attached.Id)}", ct);
    }

    /// <summary>The block an address is managed under in /ip: the address itself as /32 or /128.</summary>
    internal static string Block(string ip) =>
        IPAddress.TryParse(ip, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? $"{ip}/128" : $"{ip}/32";

    /// <summary>
    /// The pool for a new region: the first /24 of <paramref name="range"/> that no other region uses, from .2 to .254.
    /// </summary>
    internal static (string Start, string End) NextPool(string range, IEnumerable<OvhIpPool> taken)
    {
        var used = taken.Select(p => p.Start).OfType<string>().Select(start => IpRanges.Normalize($"{start}/24")).ToList();
        var subnet = IpRanges.NextFreeSubnet(range, used)
                     ?? throw new ProviderException($"The range {range} has no free /24 left for another region.");
        var prefix = subnet[..subnet.LastIndexOf('.')];
        return ($"{prefix}.2", $"{prefix}.254");
    }

    private async Task AddSubnetAsync(string networkId, string range, string region, IEnumerable<OvhIpPool> taken, CancellationToken ct)
    {
        var (start, end) = NextPool(range, taken);
        await api.PostAsync($"{NetworkPath(networkId)}/subnet",
            new OvhCreateSubnet(range, region, start, end, Dhcp: true, NoGateway: true), ct);
    }

    private async Task<string?> GetReverseAsync(string ip, CancellationToken ct)
    {
        try
        {
            var reverse = await api.GetAsync<OvhReverseIp>($"ip/{Escape(Block(ip))}/reverse/{Escape(ip)}", ct, Missing);
            return reverse?.Reverse.TrimEnd('.');
        }
        catch (ProviderException)
        {
            // Unreadable reverse DNS (e.g. a key without /ip rules) must not hide the rest of the networking view.
            return null;
        }
    }

    private async Task WaitUntilActiveAsync(string networkId, string region, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BuildTimeout;
        while (true)
        {
            var network = await GetNetworkAsync(networkId, ct);
            if (network.Regions?.FirstOrDefault(r => r.Region == region)?.Status == Active)
                return;
            if (DateTime.UtcNow > deadline)
                throw new ProviderException($"OVHcloud is still building the network {network.Name} in {region}. Try again in a minute.");
            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task<List<OvhPrivateNetwork>> ListNetworksAsync(CancellationToken ct) =>
        [.. (await api.GetAllAsync<OvhPrivateNetwork>($"{ProjectPath}/network/private", ct)).Where(n => n.Type != "public")];

    private async Task<OvhPrivateNetwork> GetNetworkAsync(string networkId, CancellationToken ct) =>
        await api.GetAsync<OvhPrivateNetwork>(NetworkPath(networkId), ct, Missing) ?? throw new NotFoundException("Private network");

    private async Task<OvhInstance> GetInstanceAsync(string externalServerId, CancellationToken ct) =>
        await api.GetAsync<OvhInstance>(InstancePath(externalServerId), ct, Missing) ?? throw new NotFoundException("Server at OVHcloud");

    /// <summary>Interfaces attach to the network's OpenStack id in the instance's region.</summary>
    private static string OpenstackId(OvhPrivateNetwork network, string region) =>
        network.Regions?.FirstOrDefault(r => r.Region == region)?.OpenstackId ?? network.Id;

    private string NetworkPath(string networkId) => $"{ProjectPath}/network/private/{Escape(networkId)}";

    private string InstancePath(string instanceId) => $"{ProjectPath}/instance/{Escape(instanceId)}";
}
