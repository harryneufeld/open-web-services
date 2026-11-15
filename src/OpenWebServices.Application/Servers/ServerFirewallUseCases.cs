using System.Globalization;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

/// <param name="Firewall">Null when the provider has no firewalls or the server does not exist there yet.</param>
/// <param name="Unavailable">Why <paramref name="Firewall"/> is null.</param>
public sealed record ServerFirewallView(ServerFirewall? Firewall, string? Unavailable);

/// <summary>A ready-made rule the UI offers with one click.</summary>
public sealed record FirewallPreset(string Name, FirewallRule Rule);

/// <summary>The inbound firewall ows manages for a server. Rules are read and written live at the provider.</summary>
public sealed class ServerFirewalls(
    AccessContext access,
    IProviderConnectionFactory connections,
    IServerRepository servers)
{
    public const int MaxRules = 50;
    public const int MaxSourcesPerRule = 100;
    public const int MaxDescriptionLength = 255;

    private static readonly string[] Everyone = [IpRanges.AnyIpv4, IpRanges.AnyIpv6];

    public static IReadOnlyList<FirewallPreset> Presets { get; } =
    [
        new("SSH", new FirewallRule(FirewallProtocol.Tcp, "22", Everyone, "SSH")),
        new("HTTP", new FirewallRule(FirewallProtocol.Tcp, "80", Everyone, "HTTP")),
        new("HTTPS", new FirewallRule(FirewallProtocol.Tcp, "443", Everyone, "HTTPS")),
        new("HTTP/3", new FirewallRule(FirewallProtocol.Udp, "443", Everyone, "HTTP/3 (QUIC)")),
        new("Ping", new FirewallRule(FirewallProtocol.Icmp, null, Everyone, "Ping")),
    ];

    public async Task<ServerFirewallView> GetAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is not { } externalId)
            return new ServerFirewallView(null, "The server has not been created at the provider yet.");

        var connection = await connections.ConnectAsync(server.GroupId, ct);
        if (connection.Firewalls is not { } firewalls)
            return new ServerFirewallView(null, $"ows does not manage firewalls at {connection.Type.DisplayName} yet.");
        return new ServerFirewallView(await firewalls.GetServerFirewallAsync(externalId, ct), null);
    }

    /// <summary>Replaces the inbound rules of the server's ows firewall, creating it on first use.</summary>
    public async Task SaveAsync(Guid serverId, IReadOnlyList<FirewallRule> rules, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        if (rules.Count == 0)
            throw new DomainException("Add at least one rule. A firewall without rules blocks all inbound traffic; remove the firewall instead to allow everything.");
        if (rules.Count > MaxRules)
            throw new DomainException($"A firewall can have at most {MaxRules} rules.");
        var normalized = rules.Select(Normalize).Distinct(RuleComparer.Instance).ToList();

        var (server, firewalls) = await ConnectAsync(serverId, ct);
        await firewalls.SetServerFirewallAsync(server.ExternalId!, normalized, ct);
    }

    /// <summary>Removes the ows firewall. Inbound traffic is then only filtered by other firewalls, if any.</summary>
    public async Task RemoveAsync(Guid serverId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var (server, firewalls) = await ConnectAsync(serverId, ct);
        await firewalls.RemoveServerFirewallAsync(server.ExternalId!, ct);
    }

    /// <summary>Validates a rule and brings ports and ranges into the form providers accept.</summary>
    public static FirewallRule Normalize(FirewallRule rule)
    {
        string? port = null;
        if (rule.Protocol != FirewallProtocol.Icmp)
            port = NormalizePort(rule.Port);

        var sources = rule.Sources.Select(s => s.Trim()).Where(s => s.Length > 0).Select(IpRanges.Normalize).Distinct().ToList();
        if (sources.Count == 0)
            throw new DomainException("Each rule needs at least one source, e.g. 0.0.0.0/0 for everyone.");
        if (sources.Count > MaxSourcesPerRule)
            throw new DomainException($"A rule can have at most {MaxSourcesPerRule} sources.");

        var description = string.IsNullOrWhiteSpace(rule.Description) ? null : rule.Description.Trim();
        if (description?.Length > MaxDescriptionLength)
            throw new DomainException($"Rule descriptions can have at most {MaxDescriptionLength} characters.");
        return new FirewallRule(rule.Protocol, port, sources, description);
    }

    private static string NormalizePort(string? value)
    {
        var text = value?.Replace(" ", "", StringComparison.Ordinal) ?? "";
        var parts = text.Split('-');
        if (parts.Length is 1 or 2 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 65535))
        {
            var from = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var to = int.Parse(parts[^1], CultureInfo.InvariantCulture);
            if (from <= to)
                return from == to ? from.ToString(CultureInfo.InvariantCulture) : $"{from.ToString(CultureInfo.InvariantCulture)}-{to.ToString(CultureInfo.InvariantCulture)}";
        }

        throw new DomainException($"'{value}' is not a port or port range such as 443 or 8000-8100 (1-65535).");
    }

    private async Task<(Server Server, IFirewallProvider Firewalls)> ConnectAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is null)
            throw new DomainException("The server has not been created at the provider yet.");
        return (server, (await connections.ConnectAsync(server.GroupId, ct)).RequireFirewalls());
    }

    private sealed class RuleComparer : IEqualityComparer<FirewallRule>
    {
        public static readonly RuleComparer Instance = new();

        public bool Equals(FirewallRule? x, FirewallRule? y) =>
            x is not null && y is not null && x.Protocol == y.Protocol && x.Port == y.Port && x.Sources.SequenceEqual(y.Sources);

        public int GetHashCode(FirewallRule obj) => HashCode.Combine(obj.Protocol, obj.Port, obj.Sources.Count);
    }
}
