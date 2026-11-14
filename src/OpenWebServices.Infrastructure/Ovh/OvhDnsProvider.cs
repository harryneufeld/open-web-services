using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud DNS record management for zones that already exist in the OVHcloud account.</summary>
internal sealed class OvhDnsProvider(OvhApiClient api) : IDnsProvider
{
    public async Task<IReadOnlyList<ProviderZone>> ListZonesAsync(CancellationToken ct)
    {
        var names = await api.GetAllAsync<string>("domain/zone", ct);
        var zones = new List<ProviderZone>(names.Count);
        foreach (var name in names)
        {
            var zone = await api.GetAsync<OvhDnsZone>($"domain/zone/{Path(name)}", ct);
            zones.Add(new ProviderZone(
                name,
                name,
                zone.Status ?? "active",
                null,
                zone.RecordCount,
                zone.NameServers ?? [],
                null));
        }

        return zones;
    }

    public Task<ProviderZone> CreateZoneAsync(string name, CancellationToken ct) =>
        throw new ProviderException("OVHcloud API v1 does not create DNS zones. Create the zone in OVHcloud first; it will be imported on the next sync.");

    public Task DeleteZoneAsync(string zoneExternalId, CancellationToken ct) =>
        throw new ProviderException("OVHcloud DNS zone termination is an account-order workflow and is not available from this connector.");

    public async Task<IReadOnlyList<DnsRecordSet>> ListRecordSetsAsync(string zoneExternalId, CancellationToken ct)
    {
        var records = await GetRecordsAsync(zoneExternalId, ct);
        return [.. records
            .GroupBy(record => (Name: RelativeName(record.SubDomain), Type: record.FieldType), RecordKeyComparer.Instance)
            .Select(group => new DnsRecordSet(
                group.Key.Name,
                group.Key.Type,
                group.Select(r => r.Ttl).Distinct().Count() == 1 ? group.First().Ttl : null,
                [.. group.Select(r => r.Target)],
                IsManagedByProvider(group.Key.Name, group.Key.Type)))
            .OrderBy(set => set.Name, StringComparer.Ordinal)
            .ThenBy(set => set.Type, StringComparer.Ordinal)];
    }

    public async Task CreateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct)
    {
        foreach (var value in recordSet.Values)
            await api.PostAsync<OvhDnsRecord>(RecordPath(zoneExternalId), ToBody(recordSet, value), ct);
        await RefreshAsync(zoneExternalId, ct);
    }

    public async Task UpdateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct)
    {
        var current = (await GetRecordsAsync(zoneExternalId, ct))
            .Where(record => RelativeName(record.SubDomain) == recordSet.Name && record.FieldType == recordSet.Type)
            .OrderBy(record => record.Id)
            .ToList();

        var shared = Math.Min(current.Count, recordSet.Values.Count);
        for (var i = 0; i < shared; i++)
            await api.PutAsync($"{RecordPath(zoneExternalId)}/{current[i].Id}", ToBody(recordSet, recordSet.Values[i]), ct);
        for (var i = shared; i < recordSet.Values.Count; i++)
            await api.PostAsync<OvhDnsRecord>(RecordPath(zoneExternalId), ToBody(recordSet, recordSet.Values[i]), ct);
        for (var i = shared; i < current.Count; i++)
            await api.DeleteAsync($"{RecordPath(zoneExternalId)}/{current[i].Id}", ct);

        await RefreshAsync(zoneExternalId, ct);
    }

    public async Task DeleteRecordSetAsync(string zoneExternalId, string name, string type, CancellationToken ct)
    {
        var records = await GetRecordsAsync(zoneExternalId, ct);
        foreach (var record in records.Where(r => RelativeName(r.SubDomain) == name && r.FieldType == type))
            await api.DeleteAsync($"{RecordPath(zoneExternalId)}/{record.Id}", ct);
        await RefreshAsync(zoneExternalId, ct);
    }

    private async Task<List<OvhDnsRecord>> GetRecordsAsync(string zone, CancellationToken ct)
    {
        // The API returns record IDs, with each record fetched separately for its editable fields.
        var ids = await api.GetAllAsync<long>(RecordPath(zone), ct);
        var records = new List<OvhDnsRecord>(ids.Count);
        foreach (var id in ids)
            records.Add(await api.GetAsync<OvhDnsRecord>($"{RecordPath(zone)}/{id}", ct));
        return records;
    }

    private Task RefreshAsync(string zone, CancellationToken ct) =>
        api.PostAsync($"domain/zone/{Path(zone)}/refresh", ct);

    private static OvhDnsRecordBody ToBody(DnsRecordSet recordSet, string value) =>
        new(recordSet.Type, SubDomain(recordSet.Name), NormalizeTarget(recordSet.Type, value), recordSet.Ttl);

    private static string NormalizeTarget(string type, string value) =>
        type == "TXT" && value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : value;

    private static string RelativeName(string? subDomain) => string.IsNullOrWhiteSpace(subDomain) ? "@" : subDomain.TrimEnd('.');
    private static string SubDomain(string name) => name is "@" or "" ? "" : name.TrimEnd('.');
    private static string Path(string value) => Uri.EscapeDataString(value);
    private static string RecordPath(string zone) => $"domain/zone/{Path(zone)}/record";

    private static bool IsManagedByProvider(string name, string type) => type == "SOA" || type == "NS" && name == "@";

    private sealed class RecordKeyComparer : IEqualityComparer<(string Name, string Type)>
    {
        public static readonly RecordKeyComparer Instance = new();
        public bool Equals((string Name, string Type) x, (string Name, string Type) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Name, string Type) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Type));
    }
}
