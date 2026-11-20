using System.Text.Json;

namespace OpenWebServices.Infrastructure.Hetzner;

// Wire models of the Hetzner Cloud API (snake_case JSON). Only the fields we use.

internal sealed record HetznerAmount(decimal Net, decimal Gross);

internal sealed record HetznerPrice(string Location, HetznerAmount PriceMonthly);

internal sealed record HetznerTypeLocation(string Name, JsonElement? Deprecation);

internal sealed record HetznerServerType(
    long Id,
    string Name,
    int Cores,
    decimal Memory,
    int Disk,
    string CpuType,
    string Architecture,
    List<HetznerPrice> Prices,
    List<HetznerTypeLocation>? Locations,
    JsonElement? Deprecation);

internal sealed record HetznerLocation(string Name, string City, string Country, string? NetworkZone = null);

/// <param name="AvailableForMigration">Types existing servers in the datacenter can change to.</param>
internal sealed record HetznerDatacenterTypes(List<long> Available, List<long>? AvailableForMigration = null);

internal sealed record HetznerDatacenter(HetznerLocation Location, HetznerDatacenterTypes ServerTypes);

internal sealed record HetznerIpv4(string Ip, string? DnsPtr = null);

internal sealed record HetznerDnsPtr(string Ip, string DnsPtr);

internal sealed record HetznerIpv6(string Ip, List<HetznerDnsPtr>? DnsPtr);

internal sealed record HetznerFirewallRef(long Id, string? Status);

internal sealed record HetznerPublicNet(HetznerIpv4? Ipv4, HetznerIpv6? Ipv6 = null, List<HetznerFirewallRef>? Firewalls = null);

internal sealed record HetznerPrivateNet(long Network, string Ip);

internal sealed record HetznerImage(string? Name, string? Description);

internal sealed record HetznerServer(
    long Id,
    string Name,
    string Status,
    HetznerPublicNet? PublicNet,
    HetznerServerType? ServerType,
    HetznerLocation? Location,
    HetznerImage? Image,
    List<HetznerPrivateNet>? PrivateNet = null,
    Dictionary<string, string>? Labels = null,
    int? PrimaryDiskSize = null);

internal sealed record HetznerServerEnvelope(HetznerServer Server);

internal sealed record HetznerChangeTypeBody(string ServerType, bool UpgradeDisk);

internal sealed record HetznerSshKey(long Id);

internal sealed record HetznerSshKeyEnvelope(HetznerSshKey SshKey);

internal sealed record HetznerCreateServerBody(
    string Name,
    string ServerType,
    string Location,
    string Image,
    long[] SshKeys,
    string UserData,
    bool StartAfterCreate);

internal sealed record HetznerCreateSshKeyBody(string Name, string PublicKey);

// DNS (zones and RRsets live in the Cloud API and use the project token)

internal sealed record HetznerAuthoritativeNameservers(List<string>? Assigned, string? DelegationStatus);

internal sealed record HetznerZone(
    long Id,
    string Name,
    string? Mode,
    int? Ttl,
    string? Status,
    int? RecordCount,
    HetznerAuthoritativeNameservers? AuthoritativeNameservers);

internal sealed record HetznerZoneEnvelope(HetznerZone Zone);

internal sealed record HetznerCreateZoneBody(string Name, string Mode);

internal sealed record HetznerRecord(string Value, string? Comment = null);

internal sealed record HetznerRRSet(string Name, string Type, int? Ttl, List<HetznerRecord> Records);

internal sealed record HetznerCreateRRSetBody(string Name, string Type, int? Ttl, List<HetznerRecord> Records);

internal sealed record HetznerSetRecordsBody(List<HetznerRecord> Records);

/// <summary>Serialized with ttl: null on purpose, which resets the RRset to the zone default.</summary>
internal sealed record HetznerChangeTtlBody(int? Ttl);

// Networking

internal sealed record HetznerChangeDnsPtrBody(string Ip, string? DnsPtr);

internal sealed record HetznerSubnet(string Type, string? IpRange, string NetworkZone);

internal sealed record HetznerNetwork(long Id, string Name, string IpRange, List<HetznerSubnet>? Subnets, List<long>? Servers);

internal sealed record HetznerNetworkEnvelope(HetznerNetwork Network);

internal sealed record HetznerCreateNetworkBody(string Name, string IpRange, Dictionary<string, string> Labels);

internal sealed record HetznerAddSubnetBody(string Type, string NetworkZone, string IpRange);

internal sealed record HetznerNetworkRefBody(long Network);

internal sealed record HetznerAction(long Id, string Status, HetznerActionError? Error);

internal sealed record HetznerActionError(string? Code, string? Message);

internal sealed record HetznerActionEnvelope(HetznerAction Action);

internal sealed record HetznerActionsEnvelope(List<HetznerAction>? Actions)
{
    public IEnumerable<HetznerAction> All => Actions ?? [];
}

// Firewalls

internal sealed record HetznerFirewallRule(
    string Direction,
    string Protocol,
    string? Port,
    List<string>? SourceIps,
    List<string>? DestinationIps,
    string? Description);

internal sealed record HetznerFirewallServerRef(long Id);

internal sealed record HetznerFirewallResource(string Type, HetznerFirewallServerRef? Server);

internal sealed record HetznerFirewall(
    long Id,
    string Name,
    Dictionary<string, string>? Labels,
    List<HetznerFirewallRule> Rules,
    List<HetznerFirewallResource>? AppliedTo);

internal sealed record HetznerCreateFirewallBody(
    string Name,
    Dictionary<string, string> Labels,
    List<HetznerFirewallRule> Rules,
    List<HetznerFirewallResource> ApplyTo);

internal sealed record HetznerSetRulesBody(List<HetznerFirewallRule> Rules);

internal sealed record HetznerFirewallResourcesBody(List<HetznerFirewallResource> RemoveFrom);

internal sealed record HetznerLabelsBody(Dictionary<string, string> Labels);

// Volumes

internal sealed record HetznerVolume(
    long Id,
    string Name,
    int Size,
    long? Server,
    HetznerLocation? Location,
    string? LinuxDevice,
    string? Status);

internal sealed record HetznerVolumeEnvelope(HetznerVolume Volume);

internal sealed record HetznerVolumeCreated(HetznerVolume Volume, HetznerAction? Action, List<HetznerAction>? NextActions);

internal sealed record HetznerCreateVolumeBody(
    string Name, int Size, long Server, bool Automount, string? Format, Dictionary<string, string> Labels);

internal sealed record HetznerAttachVolumeBody(long Server, bool Automount);

internal sealed record HetznerResizeVolumeBody(int Size);

internal sealed record HetznerRenameBody(string Name);

internal sealed record HetznerVolumePricing(HetznerAmount PricePerGbMonth);

internal sealed record HetznerPricing(string Currency, HetznerVolumePricing? Volume);

internal sealed record HetznerPricingEnvelope(HetznerPricing Pricing);
