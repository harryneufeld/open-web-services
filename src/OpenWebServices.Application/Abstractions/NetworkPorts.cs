namespace OpenWebServices.Application.Abstractions;

/// <summary>The network setup of one server as the provider reports it.</summary>
/// <param name="PublicIpv6">The server's IPv6 address or prefix, e.g. "2001:db8::/64".</param>
/// <param name="CanEditReverseDns">The provider lets ows change reverse DNS of the public addresses.</param>
/// <param name="CanManagePrivateNetworks">The provider lets ows create private networks and attach servers.</param>
public sealed record ServerNetworking(
    string? PublicIpv4,
    string? PublicIpv6,
    IReadOnlyList<PrivateNetworkAttachment> PrivateNetworks,
    IReadOnlyList<ReverseDnsEntry> ReverseDns,
    bool CanEditReverseDns,
    bool CanManagePrivateNetworks);

/// <param name="NetworkName">The network's name, or its id when the provider reports none.</param>
public sealed record PrivateNetworkAttachment(string NetworkId, string NetworkName, string Ip);

/// <param name="Ptr">The host name the address resolves back to, or null when none is set.</param>
public sealed record ReverseDnsEntry(string Ip, string? Ptr);

/// <param name="ServerIds">External ids of the servers attached to the network.</param>
public sealed record PrivateNetwork(string ExternalId, string Name, string IpRange, IReadOnlyList<string> ServerIds);

/// <summary>Addresses and private networks of servers at a provider.</summary>
public interface IServerNetworkProvider
{
    Task<ServerNetworking> GetServerNetworkingAsync(string externalServerId, CancellationToken ct);

    /// <param name="ptr">The new host name, or null to reset it to the provider default.</param>
    Task SetReverseDnsAsync(string externalServerId, string ip, string? ptr, CancellationToken ct);

    Task<IReadOnlyList<PrivateNetwork>> ListPrivateNetworksAsync(CancellationToken ct);

    /// <param name="ipRange">An RFC 1918 IPv4 range in CIDR notation, e.g. 10.0.0.0/16.</param>
    Task<PrivateNetwork> CreatePrivateNetworkAsync(string name, string ipRange, CancellationToken ct);

    /// <summary>Attaches the server, adding a subnet in the server's network zone first when the network has none.</summary>
    Task AttachToPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct);

    Task DetachFromPrivateNetworkAsync(string externalServerId, string networkId, CancellationToken ct);
}

public enum FirewallProtocol { Tcp, Udp, Icmp }

/// <summary>One inbound allow rule. Everything that no rule allows is dropped once a firewall applies to a server.</summary>
/// <param name="Port">A port ("22") or range ("8000-8100"); null for ICMP.</param>
/// <param name="Sources">Allowed source ranges in CIDR notation; 0.0.0.0/0 and ::/0 allow everyone.</param>
public sealed record FirewallRule(FirewallProtocol Protocol, string? Port, IReadOnlyList<string> Sources, string? Description = null);

/// <summary>A firewall applied to a server that ows did not create; shown, never changed.</summary>
public sealed record AppliedFirewall(string ExternalId, string Name, IReadOnlyList<FirewallRule> InboundRules);

/// <param name="ManagedRules">Inbound rules of the firewall ows manages for this server; null when there is none.</param>
/// <param name="OtherFirewalls">Further firewalls applied to the server, e.g. created in the provider's console.</param>
public sealed record ServerFirewall(IReadOnlyList<FirewallRule>? ManagedRules, IReadOnlyList<AppliedFirewall> OtherFirewalls)
{
    public bool IsFiltered => ManagedRules is not null || OtherFirewalls.Count > 0;
}

/// <summary>
/// Provider firewalls. ows manages one firewall per server and leaves every other firewall alone, so rules set up in the
/// provider's console keep working.
/// </summary>
public interface IFirewallProvider
{
    Task<ServerFirewall> GetServerFirewallAsync(string externalServerId, CancellationToken ct);

    /// <summary>Creates the server's ows firewall or replaces its rules. Outbound traffic stays unrestricted.</summary>
    Task SetServerFirewallAsync(string externalServerId, IReadOnlyList<FirewallRule> inboundRules, CancellationToken ct);

    /// <summary>Removes the server's ows firewall, if any. Safe to call for servers without one.</summary>
    Task RemoveServerFirewallAsync(string externalServerId, CancellationToken ct);
}
