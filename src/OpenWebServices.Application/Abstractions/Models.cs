using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Abstractions;

public enum PowerAction { PowerOn, Shutdown, Reboot }

public sealed record SshKeyPair(string PrivateKeyPem, string PublicKeyOpenSsh);

/// <summary>A purchasable server size at one location, normalized across providers.</summary>
public sealed record ServerOffer(
    string ProviderType,
    string Location,
    string LocationCity,
    string Country,
    string TypeName,
    int VCpus,
    string CpuKind,
    string Architecture,
    decimal MemoryGb,
    int DiskGb,
    decimal MonthlyNet,
    decimal MonthlyGross,
    string Currency,
    bool Available)
{
    /// <summary>The group the offer can be ordered with. Set by the application, not by the provider.</summary>
    public Guid GroupId { get; init; }

    public string GroupName { get; init; } = "";

    public string Key => $"{GroupId:N}/{Location}/{TypeName}";
}

public sealed record CreateServerRequest(
    string Name,
    string TypeName,
    string Location,
    string Image,
    string SshKeyId,
    string UserData);

public sealed record CreatedServer(string ExternalId, string? Ipv4);

/// <summary>A server as the provider reports it, used for import and sync.</summary>
public sealed record ProviderServer(
    string ExternalId,
    string Name,
    ObservedServerState State,
    string? Ipv4,
    string Location,
    string TypeName,
    string Image,
    decimal MonthlyNet,
    string Currency);

/// <summary>A DNS zone as the provider reports it.</summary>
public sealed record ProviderZone(
    string ExternalId,
    string Name,
    string Status,
    int? Ttl,
    int? RecordCount,
    IReadOnlyList<string> Nameservers,
    string? DelegationStatus);

/// <summary>
/// All records of one name and type (an "RRset"). This is the common denominator of Hetzner, Route 53, Azure DNS
/// and others; providers that store single records (e.g. OVH) group them into sets in their adapter.
/// </summary>
/// <param name="Name">Relative to the zone; "@" is the zone apex.</param>
/// <param name="Values">Zone-file presentation format, e.g. TXT values in quotes.</param>
/// <param name="IsManagedByProvider">Records like SOA or apex NS that the provider maintains and refuses to change.</param>
public sealed record DnsRecordSet(
    string Name,
    string Type,
    int? Ttl,
    IReadOnlyList<string> Values,
    bool IsManagedByProvider = false);

public sealed record RemoteTarget(string Host, string PrivateKeyPem, string User = "root");

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed record AppTemplate(
    string Id,
    string Name,
    string Description,
    string ComposeYaml,
    IReadOnlyList<string> GeneratedSecrets);
