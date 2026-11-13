using System.Text.RegularExpressions;

namespace OpenWebServices.Domain.Dns;

/// <summary>
/// A DNS zone hosted at a provider (e.g. a Hetzner DNS zone, a Route 53 hosted zone, an OVH zone).
/// Only the zone inventory is stored; record sets are always read live from the provider.
/// </summary>
public sealed partial class DnsZone
{
    private DnsZone() { } // EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid GroupId { get; private set; }
    public string ExternalId { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Status { get; private set; } = "";
    public int? Ttl { get; private set; }
    public int? RecordCount { get; private set; }

    /// <summary>Nameservers the provider assigned, comma-separated. Point the domain at these at the registrar.</summary>
    public string Nameservers { get; private set; } = "";

    public string? DelegationStatus { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LastSyncedAtUtc { get; private set; }

    public IReadOnlyList<string> NameserverList =>
        Nameservers.Length == 0 ? [] : Nameservers.Split(',');

    public static DnsZone Track(Guid tenantId, Guid groupId, string externalId, string name, DateTime nowUtc)
    {
        if (tenantId == Guid.Empty || groupId == Guid.Empty)
            throw new DomainException("A tenant and a provider group are required.");
        if (string.IsNullOrWhiteSpace(externalId))
            throw new DomainException("A provider zone id is required.");

        return new DnsZone
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GroupId = groupId,
            ExternalId = externalId,
            Name = NormalizeName(name),
            CreatedAtUtc = nowUtc,
            LastSyncedAtUtc = nowUtc,
        };
    }

    public void Update(
        string status, int? ttl, int? recordCount, IEnumerable<string> nameservers, string? delegationStatus, DateTime nowUtc)
    {
        Status = status;
        Ttl = ttl;
        RecordCount = recordCount;
        Nameservers = string.Join(',', nameservers);
        DelegationStatus = delegationStatus;
        LastSyncedAtUtc = nowUtc;
    }

    /// <summary>Lower-cases and strips a trailing dot; rejects anything that is not a plausible domain name.</summary>
    public static string NormalizeName(string name)
    {
        var normalized = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (!ValidDomain().IsMatch(normalized))
            throw new DomainException($"'{name}' is not a valid domain name.");
        return normalized;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9_]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex ValidDomain();
}
