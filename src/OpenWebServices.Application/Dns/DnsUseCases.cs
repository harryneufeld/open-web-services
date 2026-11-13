using System.Text.RegularExpressions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Application.Dns;

public sealed record ZoneView(DnsZone Zone, string GroupName, string ProviderName);

public sealed class DnsQueries(IDnsZoneRepository zones, IProviderGroupRepository groups, IProviderCatalog catalog)
{
    public async Task<IReadOnlyList<ZoneView>> ListZonesAsync(CancellationToken ct)
    {
        var groupsById = (await groups.ListAsync(ct)).ToDictionary(g => g.Id);
        return [.. (await zones.ListAsync(ct)).Select(z => ToView(z, groupsById.GetValueOrDefault(z.GroupId)))];
    }

    public async Task<ZoneView> GetZoneAsync(Guid zoneId, CancellationToken ct)
    {
        var zone = await zones.FindAsync(zoneId, ct) ?? throw new NotFoundException("DNS zone");
        return ToView(zone, await groups.FindAsync(zone.GroupId, ct));
    }

    /// <summary>Groups in which new zones can be created.</summary>
    public async Task<IReadOnlyList<ProviderGroup>> ListDnsGroupsAsync(CancellationToken ct) =>
        [.. (await groups.ListAsync(ct)).Where(g => g.IsEnabled(ResourceKind.Dns))];

    private ZoneView ToView(DnsZone zone, ProviderGroup? group) => new(
        zone,
        group?.Name ?? "(unknown group)",
        group is null ? "" : catalog.Find(group.ProviderType)?.DisplayName ?? group.ProviderType);
}

public sealed class CreateDnsZone(
    IProviderConnectionFactory connections,
    IProviderGroupRepository groups,
    IDnsZoneRepository zones,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Guid> ExecuteAsync(Guid groupId, string domainName, CancellationToken ct)
    {
        var group = await groups.FindAsync(groupId, ct) ?? throw new NotFoundException("Provider group");
        if (!group.IsEnabled(ResourceKind.Dns))
            throw new DomainException($"DNS is not enabled for the group '{group.Name}'.");

        var name = DnsZone.NormalizeName(domainName);
        var dns = (await connections.ConnectAsync(groupId, ct)).RequireDns();
        var created = await dns.CreateZoneAsync(name, ct);

        var zone = DnsZone.Track(groupId, created.ExternalId, created.Name, clock.UtcNow);
        zone.Update(created.Status, created.Ttl, created.RecordCount, created.Nameservers, created.DelegationStatus, clock.UtcNow);
        zones.Add(zone);
        await unitOfWork.SaveChangesAsync(ct);
        return zone.Id;
    }
}

/// <summary>Deletes the zone at the provider (with all its records) and stops tracking it.</summary>
public sealed class DeleteDnsZone(
    IProviderConnectionFactory connections,
    IDnsZoneRepository zones,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid zoneId, CancellationToken ct)
    {
        var zone = await zones.FindAsync(zoneId, ct) ?? throw new NotFoundException("DNS zone");
        var dns = (await connections.ConnectAsync(zone.GroupId, ct)).RequireDns();
        await dns.DeleteZoneAsync(zone.ExternalId, ct);
        zones.Remove(zone);
        await unitOfWork.SaveChangesAsync(ct);
    }
}

/// <summary>Record sets are read and written live at the provider; only the zone inventory is stored.</summary>
public sealed partial class DnsRecords(
    IProviderConnectionFactory connections,
    IDnsZoneRepository zones,
    ISyncScheduler syncScheduler)
{
    public const int MinTtl = 60;

    public static IReadOnlyList<string> SupportedTypes { get; } =
        ["A", "AAAA", "CNAME", "MX", "TXT", "SRV", "CAA", "NS", "PTR", "TLSA", "DS", "HTTPS", "SVCB"];

    public async Task<IReadOnlyList<DnsRecordSet>> ListAsync(Guid zoneId, CancellationToken ct)
    {
        var (zone, dns) = await ConnectAsync(zoneId, ct);
        var sets = await dns.ListRecordSetsAsync(zone.ExternalId, ct);
        return [.. sets.OrderBy(s => s.Name == "@" ? "" : s.Name, StringComparer.Ordinal).ThenBy(s => s.Type)];
    }

    public async Task CreateAsync(Guid zoneId, DnsRecordSet recordSet, CancellationToken ct)
    {
        var normalized = Normalize(recordSet);
        var (zone, dns) = await ConnectAsync(zoneId, ct);
        await dns.CreateRecordSetAsync(zone.ExternalId, normalized, ct);
        syncScheduler.RequestSync(zone.GroupId);
    }

    public async Task UpdateAsync(Guid zoneId, DnsRecordSet recordSet, CancellationToken ct)
    {
        var normalized = Normalize(recordSet);
        var (zone, dns) = await ConnectAsync(zoneId, ct);
        await dns.UpdateRecordSetAsync(zone.ExternalId, normalized, ct);
        syncScheduler.RequestSync(zone.GroupId);
    }

    public async Task DeleteAsync(Guid zoneId, string name, string type, CancellationToken ct)
    {
        var (zone, dns) = await ConnectAsync(zoneId, ct);
        await dns.DeleteRecordSetAsync(zone.ExternalId, name, type, ct);
        syncScheduler.RequestSync(zone.GroupId);
    }

    /// <summary>Validates user input and brings it into the shape every provider adapter expects.</summary>
    public static DnsRecordSet Normalize(DnsRecordSet input)
    {
        var name = input.Name.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0)
            name = "@";
        if (name != "@" && !ValidRecordName().IsMatch(name))
            throw new DomainException($"'{input.Name}' is not a valid record name. Use '@' for the zone itself.");

        var type = input.Type.Trim().ToUpperInvariant();
        if (!SupportedTypes.Contains(type))
            throw new DomainException($"Record type '{input.Type}' is not supported.");
        if (input.Ttl is < MinTtl)
            throw new DomainException($"TTL must be at least {MinTtl} seconds.");

        var values = input.Values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList();
        if (values.Count == 0)
            throw new DomainException("At least one value is required.");
        if (type == "CNAME" && values.Count > 1)
            throw new DomainException("A CNAME record can only have one value.");
        if (type == "TXT")
            values = [.. values.Select(QuoteTxt)];

        return new DnsRecordSet(name, type, input.Ttl, values);
    }

    /// <summary>
    /// TXT values go to providers in zone-file format: quoted, in strings of at most 255 characters.
    /// Values the user already quoted are passed through unchanged.
    /// </summary>
    public static string QuoteTxt(string value) =>
        value.StartsWith('"')
            ? value
            : string.Join(' ', value.Chunk(255).Select(c => $"\"{new string(c).Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));

    private async Task<(DnsZone Zone, IDnsProvider Dns)> ConnectAsync(Guid zoneId, CancellationToken ct)
    {
        var zone = await zones.FindAsync(zoneId, ct) ?? throw new NotFoundException("DNS zone");
        return (zone, (await connections.ConnectAsync(zone.GroupId, ct)).RequireDns());
    }

    [GeneratedRegex(@"^(\*\.)?([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)(\.[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)*$|^\*$")]
    private static partial Regex ValidRecordName();
}
