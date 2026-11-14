using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud DNS record management for zones that already exist in the OVHcloud account.</summary>
internal sealed class OvhDnsProvider(OvhApiClient api) : IDnsProvider
{
    private const int MaxConcurrentRequests = 6;

    public async Task<IReadOnlyList<ProviderZone>> ListZonesAsync(CancellationToken ct)
    {
        var names = await api.GetAllAsync<string>("domain/zone", ct);
        var zones = await GetEachAsync<string, OvhDnsZone>(names, name => $"domain/zone/{Escape(name)}", ct);
        return [.. zones.Select(zone => new ProviderZone(
            zone.Name,
            zone.Name,
            zone.Status ?? "active",
            null,
            zone.RecordCount,
            zone.NameServers ?? [],
            null))];
    }

    public Task<ProviderZone> CreateZoneAsync(string name, CancellationToken ct) =>
        throw new ProviderException("OVHcloud API v1 does not create DNS zones. Create the zone in OVHcloud first; it will be imported on the next sync.");

    public Task DeleteZoneAsync(string zoneExternalId, CancellationToken ct) =>
        throw new ProviderException("OVHcloud DNS zone termination is an account-order workflow and is not available from this connector.");

    public async Task<IReadOnlyList<DnsRecordSet>> ListRecordSetsAsync(string zoneExternalId, CancellationToken ct)
    {
        var records = await GetRecordsAsync(zoneExternalId, filter: null, ct);
        return [.. records
            .GroupBy(record => (Name: RelativeName(record.SubDomain).ToLowerInvariant(), Type: record.FieldType.ToUpperInvariant()))
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
        // OVH stores one record per value; existing records are rewritten in place, extras added or removed.
        var current = (await GetRecordsAsync(zoneExternalId, (recordSet.Name, recordSet.Type), ct))
            .OrderBy(record => record.Id)
            .ToList();

        var shared = Math.Min(current.Count, recordSet.Values.Count);
        for (var i = 0; i < shared; i++)
            await api.PutAsync(RecordPath(zoneExternalId, current[i].Id), ToBody(recordSet, recordSet.Values[i]), ct);
        for (var i = shared; i < recordSet.Values.Count; i++)
            await api.PostAsync<OvhDnsRecord>(RecordPath(zoneExternalId), ToBody(recordSet, recordSet.Values[i]), ct);
        for (var i = shared; i < current.Count; i++)
            await api.DeleteAsync(RecordPath(zoneExternalId, current[i].Id), ct);

        await RefreshAsync(zoneExternalId, ct);
    }

    public async Task DeleteRecordSetAsync(string zoneExternalId, string name, string type, CancellationToken ct)
    {
        foreach (var record in await GetRecordsAsync(zoneExternalId, (name, type), ct))
            await api.DeleteAsync(RecordPath(zoneExternalId, record.Id), ct);
        await RefreshAsync(zoneExternalId, ct);
    }

    /// <summary>
    /// The API lists record IDs only; each record is then fetched for its fields. A filter narrows the ID list on
    /// the server, and is re-checked locally because OVH matches it case-insensitively.
    /// </summary>
    private async Task<IReadOnlyList<OvhDnsRecord>> GetRecordsAsync(
        string zone, (string Name, string Type)? filter, CancellationToken ct)
    {
        var query = filter is { } f ? $"?fieldType={Escape(f.Type)}&subDomain={Escape(SubDomain(f.Name))}" : "";
        var ids = await api.GetAllAsync<long>(RecordPath(zone) + query, ct);
        var records = await GetEachAsync<long, OvhDnsRecord>(ids, id => RecordPath(zone, id), ct);
        return filter is not { } wanted
            ? records
            : [.. records.Where(record =>
                string.Equals(RelativeName(record.SubDomain), wanted.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.FieldType, wanted.Type, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Fetches one resource per key with bounded concurrency, keeping the key order; vanished ones are skipped.</summary>
    private async Task<IReadOnlyList<T>> GetEachAsync<TKey, T>(
        IReadOnlyList<TKey> keys, Func<TKey, string> path, CancellationToken ct) where T : class
    {
        var results = new T?[keys.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, keys.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentRequests, CancellationToken = ct },
            async (i, token) => results[i] = await api.GetAsync<T>(path(keys[i]), token, allowNotFound: true));
        return [.. results.OfType<T>()];
    }

    private Task RefreshAsync(string zone, CancellationToken ct) =>
        api.PostAsync($"domain/zone/{Escape(zone)}/refresh", ct);

    private static OvhDnsRecordBody ToBody(DnsRecordSet recordSet, string value) =>
        new(recordSet.Type, SubDomain(recordSet.Name), NormalizeTarget(recordSet.Type, value), recordSet.Ttl);

    private static string NormalizeTarget(string type, string value) =>
        type == "TXT" && value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : value;

    private static string RelativeName(string? subDomain) => string.IsNullOrWhiteSpace(subDomain) ? "@" : subDomain.TrimEnd('.');
    private static string SubDomain(string name) => name is "@" or "" ? "" : name.TrimEnd('.');
    private static string RecordPath(string zone) => $"domain/zone/{Escape(zone)}/record";
    private static string RecordPath(string zone, long id) => $"{RecordPath(zone)}/{id}";

    private static bool IsManagedByProvider(string name, string type) => type == "SOA" || type == "NS" && name == "@";
}
