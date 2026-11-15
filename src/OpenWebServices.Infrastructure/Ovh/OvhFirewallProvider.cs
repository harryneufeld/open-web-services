using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Servers;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>
/// OVHcloud Edge Network Firewall on the instance's public IPv4. That firewall is stateless, filters IPv4 only, holds
/// 20 rules (sequence 0-19) evaluated in order, and lets through whatever no rule matches. To keep the same meaning as
/// other providers (only what is allowed comes in, outbound traffic keeps working), ows owns the whole rule set:
/// <list type="bullet">
/// <item>0-2: replies to the server's own connections (TCP established, DNS and NTP answers),</item>
/// <item>3-18: the user's rules, one sequence per IPv4 source,</item>
/// <item>19: deny all other IPv4 traffic.</item>
/// </list>
/// A rule set without this frame was made in the OVHcloud Control Panel; ows shows it and does not overwrite it.
/// </summary>
internal sealed partial class OvhFirewallProvider(OvhApiClient api, string projectId) : IFirewallProvider
{
    internal const int FirstUserSequence = 3;
    internal const int LastUserSequence = 18;
    internal const int DenySequence = 19;
    private const string AnySource = "0.0.0.0/0";
    private static readonly HttpStatusCode[] Missing = [HttpStatusCode.NotFound];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    internal static readonly FirewallLimits Limits = new(
        FiltersIpv6: false,
        StoresDescriptions: false,
        MaxRules: LastUserSequence - FirstUserSequence + 1,
        Note: "OVHcloud's Edge Network Firewall filters IPv4 only and does not track connections. ows adds rules so " +
              "replies to the server's own connections (TCP, DNS, NTP) still get through, and blocks all other IPv4 " +
              "traffic. Each IPv4 source counts as one rule. IPv6 is not filtered: use the server's own firewall for it.");

    /// <summary>How often rule and firewall states are polled while OVHcloud applies changes; tests set it to zero.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public async Task<ServerFirewall> GetServerFirewallAsync(string externalServerId, CancellationToken ct)
    {
        var ip = await PublicIpv4Async(externalServerId, ct);
        var firewall = await api.GetAsync<OvhFirewallIp>(FirewallPath(ip), ct, Missing);
        if (firewall is null || !firewall.Enabled)
            return new ServerFirewall(null, []) { Limits = Limits };

        var rules = await ReadRulesAsync(ip, ct);
        if (IsOwsLayout(rules))
            return new ServerFirewall(FromOvh(rules), []) { Limits = Limits };

        var other = new AppliedFirewall(ip, "Edge Network Firewall (OVHcloud Control Panel)",
            [.. rules.Where(r => r.Action == "permit").OrderBy(r => r.Sequence).Select(ToRule).OfType<FirewallRule>()]);
        return new ServerFirewall(null, [other]) { Limits = Limits };
    }

    public async Task SetServerFirewallAsync(string externalServerId, IReadOnlyList<FirewallRule> inboundRules, CancellationToken ct)
    {
        var desired = Layout(inboundRules);
        var ip = await PublicIpv4Async(externalServerId, ct);
        var firewall = await api.GetAsync<OvhFirewallIp>(FirewallPath(ip), ct, Missing);
        if (firewall is null)
        {
            await api.PostAsync($"ip/{Escape(Block(ip))}/firewall", new OvhAddFirewallIp(ip), ct);
            firewall = new OvhFirewallIp(ip, false, null);
        }

        var existing = await ReadRulesAsync(ip, ct);
        if (existing.Count > 0 && !IsOwsLayout(existing))
            throw new ProviderException(
                "This address already has Edge Network Firewall rules from the OVHcloud Control Panel. Remove them there first; ows does not overwrite them.");

        // Sequences are unique: a changed rule is deleted, and only re-created once OVHcloud has removed it.
        var existingBySequence = existing.ToDictionary(r => r.Sequence);
        var stale = existing.Where(r => !desired.TryGetValue(r.Sequence, out var wanted) || !Same(r, wanted)).Select(r => r.Sequence).ToList();
        foreach (var sequence in stale)
            await api.DeleteAsync($"{RulesPath(ip)}/{sequence}", ct);
        await WaitAsync(async () => (await ListSequencesAsync(ip, ct)).Intersect(stale).Any() == false, "removing old rules", ct);

        foreach (var (sequence, rule) in desired.OrderBy(d => d.Key))
        {
            if (!existingBySequence.ContainsKey(sequence) || stale.Contains(sequence))
                await api.PostAsync(RulesPath(ip), rule, ct);
        }

        // Turning the firewall on before every rule is active would briefly drop allowed traffic.
        await WaitAsync(async () => (await api.GetAllAsync<long>($"{RulesPath(ip)}?state=creationPending", ct)).Count == 0, "applying the rules", ct);
        if (!firewall.Enabled)
            await api.PutAsync(FirewallPath(ip), new OvhEnableFirewall(true), ct);
    }

    public async Task RemoveServerFirewallAsync(string externalServerId, CancellationToken ct)
    {
        var ip = await PublicIpv4Async(externalServerId, ct);
        var firewall = await api.GetAsync<OvhFirewallIp>(FirewallPath(ip), ct, Missing);
        if (firewall is null)
            return;

        var rules = await ReadRulesAsync(ip, ct);
        if (rules.Count > 0 && !IsOwsLayout(rules))
            throw new ProviderException("The Edge Network Firewall of this address was set up in the OVHcloud Control Panel. Change it there.");
        if (firewall.Enabled)
            await api.PutAsync(FirewallPath(ip), new OvhEnableFirewall(false), ct);
        foreach (var rule in rules)
            await api.DeleteAsync($"{RulesPath(ip)}/{rule.Sequence}", ct);
    }

    /// <summary>The complete rule set ows writes, by sequence.</summary>
    internal static Dictionary<long, OvhCreateFirewallRule> Layout(IReadOnlyList<FirewallRule> inboundRules)
    {
        var rules = new Dictionary<long, OvhCreateFirewallRule>
        {
            [0] = new("0", "permit", "tcp", TcpOption: new OvhTcpOption("established")),
            [1] = new("1", "permit", "udp", SourcePort: 53),
            [2] = new("2", "permit", "udp", SourcePort: 123),
            [DenySequence] = new(DenySequence.ToString(CultureInfo.InvariantCulture), "deny", "ipv4"),
        };

        var sequence = FirstUserSequence;
        foreach (var rule in inboundRules)
        {
            var sources = rule.Sources.Where(s => !s.Contains(':')).ToList(); // IPv6 is not filtered here
            foreach (var source in sources)
            {
                if (sequence > LastUserSequence)
                {
                    throw new ProviderException(
                        $"OVHcloud's Edge Network Firewall fits {Limits.MaxRules} rules, counting each IPv4 source separately. Combine sources into ranges or remove rules.");
                }

                var (port, range) = Ports(rule);
                rules[sequence] = new OvhCreateFirewallRule(
                    sequence.ToString(CultureInfo.InvariantCulture), "permit", rule.Protocol.ToString().ToLowerInvariant(),
                    source == AnySource ? null : source, port, range);
                sequence++;
            }
        }

        return rules;
    }

    /// <summary>The user's rules from a rule set ows wrote; rules for the same protocol and port merge their sources.</summary>
    internal static IReadOnlyList<FirewallRule> FromOvh(IEnumerable<OvhFirewallRule> rules)
    {
        var result = new List<FirewallRule>();
        foreach (var rule in rules.Where(r => r.Sequence is >= FirstUserSequence and <= LastUserSequence).OrderBy(r => r.Sequence))
        {
            if (ToRule(rule) is not { } mapped)
                continue;
            var last = result.Count > 0 ? result[^1] : null;
            if (last is not null && last.Protocol == mapped.Protocol && last.Port == mapped.Port)
                result[^1] = last with { Sources = [.. last.Sources, .. mapped.Sources] };
            else
                result.Add(mapped);
        }

        return result;
    }

    private static FirewallRule? ToRule(OvhFirewallRule rule)
    {
        FirewallProtocol? protocol = rule.Protocol switch
        {
            "tcp" => FirewallProtocol.Tcp,
            "udp" => FirewallProtocol.Udp,
            "icmp" => FirewallProtocol.Icmp,
            _ => null,
        };
        if (protocol is not { } known)
            return null;
        var source = string.IsNullOrEmpty(rule.Source) || rule.Source.Equals("any", StringComparison.OrdinalIgnoreCase) ? AnySource : rule.Source;
        return new FirewallRule(known, known == FirewallProtocol.Icmp ? null : ParsePort(rule.DestinationPort), [source]);
    }

    /// <summary>OVHcloud reports ports as text such as "eq 22" or "range 8000 8100"; only the numbers matter.</summary>
    internal static string? ParsePort(string? text)
    {
        var numbers = Numbers().Matches(text ?? "").Select(m => m.Value).ToList();
        return numbers.Count switch
        {
            0 => null,
            1 => numbers[0],
            _ => $"{numbers[0]}-{numbers[1]}",
        };
    }

    private static (long? Port, OvhRange? Range) Ports(FirewallRule rule)
    {
        if (rule.Protocol == FirewallProtocol.Icmp || rule.Port is null)
            return (null, null);
        var parts = rule.Port.Split('-');
        var from = long.Parse(parts[0], CultureInfo.InvariantCulture);
        return parts.Length == 1 ? (from, null) : (null, new OvhRange(from, long.Parse(parts[1], CultureInfo.InvariantCulture)));
    }

    /// <summary>Whether an existing rule already matches what ows would write at its sequence.</summary>
    private static bool Same(OvhFirewallRule existing, OvhCreateFirewallRule wanted)
    {
        var source = string.IsNullOrEmpty(existing.Source) || existing.Source.Equals("any", StringComparison.OrdinalIgnoreCase) ? null : existing.Source;
        var port = wanted.DestinationPort?.ToString(CultureInfo.InvariantCulture)
                   ?? (wanted.DestinationPortRange is { } r ? $"{r.From}-{r.To}" : null);
        return existing.Action == wanted.Action
               && existing.Protocol == wanted.Protocol
               && NormalizeSource(source) == NormalizeSource(wanted.Source)
               && ParsePort(existing.DestinationPort) == port
               && ParsePort(existing.SourcePort) == wanted.SourcePort?.ToString(CultureInfo.InvariantCulture)
               && (existing.TcpOption?.Contains("established", StringComparison.OrdinalIgnoreCase) ?? false) == (wanted.TcpOption is not null);
    }

    private static string? NormalizeSource(string? source) => source is null ? null : IpRanges.Normalize(source);

    /// <summary>The frame ows writes around the user's rules: established TCP first, deny everything last.</summary>
    private static bool IsOwsLayout(IReadOnlyCollection<OvhFirewallRule> rules) =>
        rules.Any(r => r.Sequence == 0 && r.Action == "permit" && r.Protocol == "tcp")
        && rules.Any(r => r.Sequence == DenySequence && r.Action == "deny" && r.Protocol == "ipv4");

    private async Task<List<OvhFirewallRule>> ReadRulesAsync(string ip, CancellationToken ct)
    {
        var rules = new List<OvhFirewallRule>();
        foreach (var sequence in await ListSequencesAsync(ip, ct))
        {
            if (await api.GetAsync<OvhFirewallRule>($"{RulesPath(ip)}/{sequence}", ct, Missing) is { } rule
                && rule.State != "removalPending")
                rules.Add(rule);
        }

        return rules;
    }

    private Task<List<long>> ListSequencesAsync(string ip, CancellationToken ct) => api.GetAllAsync<long>(RulesPath(ip), ct);

    private async Task<string> PublicIpv4Async(string externalServerId, CancellationToken ct)
    {
        var instance = await api.GetAsync<OvhInstance>($"cloud/project/{Escape(projectId)}/instance/{Escape(externalServerId)}", ct, Missing)
                       ?? throw new NotFoundException("Server at OVHcloud");
        return instance.IpAddresses?.FirstOrDefault(a => a.Type == "public" && a.Version == 4)?.Ip
               ?? throw new ProviderException("The server has no public IPv4 address, so OVHcloud's Edge Network Firewall cannot filter it.");
    }

    private static async Task WaitAsync(Func<Task<bool>> done, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
                throw new ProviderException($"OVHcloud is still {what}. Reload the page in a minute to see the result.");
            await Task.Delay(PollInterval, ct);
        }
    }

    private static string Block(string ip) => $"{ip}/32";

    private static string FirewallPath(string ip) => $"ip/{Escape(Block(ip))}/firewall/{Escape(ip)}";

    private static string RulesPath(string ip) => $"{FirewallPath(ip)}/rule";

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}
