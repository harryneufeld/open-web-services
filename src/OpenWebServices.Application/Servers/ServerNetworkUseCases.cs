using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

/// <param name="Networking">Null when the provider exposes no networking or the server does not exist there yet.</param>
/// <param name="AvailableNetworks">Private networks the server can join; empty unless the provider manages them.</param>
/// <param name="Unavailable">Why <paramref name="Networking"/> is null.</param>
public sealed record ServerNetworkView(
    ServerNetworking? Networking,
    IReadOnlyList<PrivateNetwork> AvailableNetworks,
    string? Unavailable);

/// <summary>Addresses, reverse DNS and private networks of a server. Read and written live at the provider.</summary>
public sealed partial class ServerNetworks(
    AccessContext access,
    IProviderConnectionFactory connections,
    IServerRepository servers,
    ISyncScheduler syncScheduler)
{
    public const int MaxNetworkNameLength = 63;

    public async Task<ServerNetworkView> GetAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is not { } externalId)
            return new ServerNetworkView(null, [], "The server has not been created at the provider yet.");

        var connection = await connections.ConnectAsync(server.GroupId, ct);
        if (connection.Networks is not { } networks)
            return new ServerNetworkView(null, [], $"ows does not manage networking at {connection.Type.DisplayName} yet.");

        var networking = await networks.GetServerNetworkingAsync(externalId, ct);
        var available = networking.CanManagePrivateNetworks
            ? (await networks.ListPrivateNetworksAsync(ct)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return new ServerNetworkView(networking, available, null);
    }

    /// <param name="ptr">The host name, or blank to reset to the provider default.</param>
    public async Task SetReverseDnsAsync(Guid serverId, string ip, string? ptr, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        if (!IPAddress.TryParse(ip.Trim(), out var address))
            throw new DomainException($"'{ip}' is not an IP address.");
        var hostName = NormalizeHostName(ptr);

        var (server, networks) = await ConnectAsync(serverId, ct);
        await networks.SetReverseDnsAsync(server.ExternalId!, address.ToString(), hostName, ct);
    }

    /// <summary>Creates a private network and attaches the server to it.</summary>
    public async Task CreateNetworkAsync(Guid serverId, string name, string ipRange, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var trimmed = name.Trim();
        if (!ValidNetworkName().IsMatch(trimmed))
            throw new DomainException($"Network name must be 1-{MaxNetworkNameLength} characters: letters, digits, '.', '_' and '-'.");
        var range = NormalizePrivateRange(ipRange);

        var (server, networks) = await ConnectAsync(serverId, ct);
        var network = await networks.CreatePrivateNetworkAsync(trimmed, range, ct);
        await networks.AttachToPrivateNetworkAsync(server.ExternalId!, network.ExternalId, ct);
        syncScheduler.RequestSync(server.GroupId);
    }

    public async Task AttachAsync(Guid serverId, string networkId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var (server, networks) = await ConnectAsync(serverId, ct);
        await networks.AttachToPrivateNetworkAsync(server.ExternalId!, networkId, ct);
        syncScheduler.RequestSync(server.GroupId);
    }

    public async Task DetachAsync(Guid serverId, string networkId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var (server, networks) = await ConnectAsync(serverId, ct);
        await networks.DetachFromPrivateNetworkAsync(server.ExternalId!, networkId, ct);
        syncScheduler.RequestSync(server.GroupId);
    }

    /// <summary>A private IPv4 range of /8 to /24, so subnets for each network zone fit into it.</summary>
    public static string NormalizePrivateRange(string ipRange)
    {
        var (address, prefix) = IpRanges.Parse(ipRange);
        if (!IpRanges.IsPrivateIpv4(address, prefix))
            throw new DomainException("Use a private IPv4 range inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16.");
        if (prefix > 24)
            throw new DomainException("The range must be at least a /24, e.g. 10.0.0.0/16.");
        return IpRanges.Normalize(ipRange);
    }

    public static string? NormalizeHostName(string? value)
    {
        var name = value?.Trim().TrimEnd('.').ToLowerInvariant() ?? "";
        if (name.Length == 0)
            return null;
        name = new IdnMapping().GetAscii(name);
        if (name.Length > 253 || !ValidHostName().IsMatch(name))
            throw new DomainException($"'{value}' is not a valid host name such as mail.example.com.");
        return name;
    }

    private async Task<(Server Server, IServerNetworkProvider Networks)> ConnectAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is null)
            throw new DomainException("The server has not been created at the provider yet.");
        return (server, (await connections.ConnectAsync(server.GroupId, ct)).RequireNetworks());
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,63}$")]
    private static partial Regex ValidNetworkName();

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex ValidHostName();
}
