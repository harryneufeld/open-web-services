using System.Globalization;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>Hetzner DNS via the Cloud API (<c>/zones</c>, <c>/zones/{id}/rrsets</c>), using the project token.</summary>
internal sealed class HetznerDnsProvider(HetznerApiClient api) : IDnsProvider
{
    public async Task<IReadOnlyList<ProviderZone>> ListZonesAsync(CancellationToken ct) =>
        [.. (await api.GetAllAsync<HetznerZone>("zones", "zones", ct)).Select(ToProviderZone)];

    public async Task<ProviderZone> CreateZoneAsync(string name, CancellationToken ct)
    {
        var created = await api.PostAsync<HetznerZoneEnvelope>("zones", new HetznerCreateZoneBody(name, "primary"), ct);
        return ToProviderZone(created.Zone);
    }

    public Task DeleteZoneAsync(string zoneExternalId, CancellationToken ct) =>
        api.DeleteAsync(ZonePath(zoneExternalId), ct);

    public async Task<IReadOnlyList<DnsRecordSet>> ListRecordSetsAsync(string zoneExternalId, CancellationToken ct)
    {
        var sets = await api.GetAllAsync<HetznerRRSet>($"{ZonePath(zoneExternalId)}/rrsets", "rrsets", ct);
        return [.. sets.Select(s => new DnsRecordSet(
            s.Name, s.Type, s.Ttl, [.. s.Records.Select(r => r.Value)], IsManagedByProvider(s.Name, s.Type)))];
    }

    public Task CreateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct) =>
        api.PostAsync(
            $"{ZonePath(zoneExternalId)}/rrsets",
            new HetznerCreateRRSetBody(recordSet.Name, recordSet.Type, recordSet.Ttl, ToRecords(recordSet)),
            ct);

    public async Task UpdateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct)
    {
        var path = RRSetPath(zoneExternalId, recordSet.Name, recordSet.Type);
        await api.PostAsync($"{path}/actions/set_records", new HetznerSetRecordsBody(ToRecords(recordSet)), ct);
        await api.PostAsync($"{path}/actions/change_ttl", new HetznerChangeTtlBody(recordSet.Ttl), ct, includeNulls: true);
    }

    public Task DeleteRecordSetAsync(string zoneExternalId, string name, string type, CancellationToken ct) =>
        api.DeleteAsync(RRSetPath(zoneExternalId, name, type), ct);

    private static ProviderZone ToProviderZone(HetznerZone zone) => new(
        zone.Id.ToString(CultureInfo.InvariantCulture),
        zone.Name,
        zone.Status ?? "unknown",
        zone.Ttl,
        zone.RecordCount,
        zone.AuthoritativeNameservers?.Assigned ?? [],
        zone.AuthoritativeNameservers?.DelegationStatus);

    private static List<HetznerRecord> ToRecords(DnsRecordSet recordSet) =>
        [.. recordSet.Values.Select(v => new HetznerRecord(v))];

    /// <summary>Hetzner maintains SOA and the apex NS set itself.</summary>
    private static bool IsManagedByProvider(string name, string type) =>
        type == "SOA" || (type == "NS" && name == "@");

    private static string ZonePath(string zoneExternalId) =>
        $"zones/{HetznerServerProvider.ParseId(zoneExternalId)}";

    private static string RRSetPath(string zoneExternalId, string name, string type) =>
        $"{ZonePath(zoneExternalId)}/rrsets/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(type)}";
}
