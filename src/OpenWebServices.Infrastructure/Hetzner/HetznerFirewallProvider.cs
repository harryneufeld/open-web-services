using System.Globalization;
using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Hetzner.HetznerServerProvider;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>
/// Hetzner Cloud firewalls. ows keeps one firewall per server, named "ows-server-{id}" and labelled with the server id;
/// firewalls created elsewhere are reported but never changed. Hetzner drops all inbound traffic no rule allows as
/// soon as any firewall applies, and allows all outbound traffic while a firewall has no outbound rules.
/// </summary>
internal sealed class HetznerFirewallProvider(HetznerApiClient api) : IFirewallProvider
{
    internal const string ServerLabel = "ows-server";
    private const string ServerResource = "server";
    private const int DeleteAttempts = 5;

    public async Task<ServerFirewall> GetServerFirewallAsync(string externalServerId, CancellationToken ct)
    {
        var serverId = ParseId(externalServerId);
        var firewalls = await api.GetAllAsync<HetznerFirewall>("firewalls", "firewalls", ct);
        var managed = FindManaged(firewalls, serverId);
        var others = firewalls.Where(f => f.Id != managed?.Id && IsAppliedTo(f, serverId))
            .Select(f => new AppliedFirewall(Id(f.Id), f.Name, InboundRules(f)))
            .ToList();
        // A managed firewall someone detached in the console does not filter anything; show it as absent.
        return new ServerFirewall(managed is not null && IsAppliedTo(managed, serverId) ? InboundRules(managed) : null, others);
    }

    public async Task SetServerFirewallAsync(string externalServerId, IReadOnlyList<FirewallRule> inboundRules, CancellationToken ct)
    {
        var serverId = ParseId(externalServerId);
        var rules = inboundRules.Select(ToHetzner).ToList();
        var managed = FindManaged(await api.GetAllAsync<HetznerFirewall>("firewalls", "firewalls", ct), serverId);
        var target = new HetznerFirewallResource(ServerResource, new HetznerFirewallServerRef(serverId));

        if (managed is null)
        {
            // The answer lists the actions that apply the new firewall to the server.
            await WaitAllAsync(await api.PostAsync<HetznerActionsEnvelope>("firewalls", new HetznerCreateFirewallBody(
                $"ows-server-{Id(serverId)}", new() { [ServerLabel] = Id(serverId) }, rules, [target]), ct), ct);
            return;
        }

        await WaitAllAsync(await api.PostAsync<HetznerActionsEnvelope>(
            $"firewalls/{managed.Id}/actions/set_rules", new HetznerSetRulesBody(rules), ct), ct);
        if (!IsAppliedTo(managed, serverId))
        {
            await WaitAllAsync(await api.PostAsync<HetznerActionsEnvelope>(
                $"firewalls/{managed.Id}/actions/apply_to_resources", new { ApplyTo = new[] { target } }, ct), ct);
        }
    }

    public async Task RemoveServerFirewallAsync(string externalServerId, CancellationToken ct)
    {
        var serverId = ParseId(externalServerId);
        if (FindManaged(await api.GetAllAsync<HetznerFirewall>("firewalls", "firewalls", ct), serverId) is not { } managed)
            return;

        if (IsAppliedTo(managed, serverId))
        {
            await WaitAllAsync(await api.PostAsync<HetznerActionsEnvelope>(
                $"firewalls/{managed.Id}/actions/remove_from_resources",
                new HetznerFirewallResourcesBody([new HetznerFirewallResource(ServerResource, new HetznerFirewallServerRef(serverId))]),
                ct), ct);
        }

        // Hetzner refuses to delete a firewall for a moment after it was detached ("resource_in_use").
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await api.DeleteAsync($"firewalls/{managed.Id}", ct);
                return;
            }
            catch (ProviderException) when (attempt < DeleteAttempts)
            {
                await Task.Delay(HetznerApiClient.ActionPollInterval, ct);
            }
        }
    }

    internal static FirewallRule FromHetzner(HetznerFirewallRule rule) => new(
        rule.Protocol switch
        {
            "tcp" => FirewallProtocol.Tcp,
            "udp" => FirewallProtocol.Udp,
            _ => FirewallProtocol.Icmp,
        },
        rule.Port,
        rule.SourceIps ?? [],
        string.IsNullOrEmpty(rule.Description) ? ProtocolNote(rule.Protocol) : rule.Description);

    internal static HetznerFirewallRule ToHetzner(FirewallRule rule) => new(
        "in",
        rule.Protocol.ToString().ToLowerInvariant(),
        rule.Protocol == FirewallProtocol.Icmp ? null : rule.Port,
        [.. rule.Sources],
        null,
        rule.Description);

    private static IReadOnlyList<FirewallRule> InboundRules(HetznerFirewall firewall) =>
        [.. firewall.Rules.Where(r => r.Direction == "in").Select(FromHetzner)];

    /// <summary>ESP and GRE have no own protocol in ows; their rules show as ICMP-like entries with a note.</summary>
    private static string? ProtocolNote(string protocol) => protocol is "tcp" or "udp" or "icmp" ? null : protocol.ToUpperInvariant();

    private static HetznerFirewall? FindManaged(IEnumerable<HetznerFirewall> firewalls, long serverId) =>
        firewalls.FirstOrDefault(f => f.Labels?.GetValueOrDefault(ServerLabel) == Id(serverId));

    private static bool IsAppliedTo(HetznerFirewall firewall, long serverId) =>
        firewall.AppliedTo?.Any(r => r.Type == ServerResource && r.Server?.Id == serverId) ?? false;

    private async Task WaitAllAsync(HetznerActionsEnvelope envelope, CancellationToken ct)
    {
        foreach (var action in envelope.All)
            await api.WaitAsync(action, ct);
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
