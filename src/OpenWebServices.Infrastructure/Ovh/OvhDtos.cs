namespace OpenWebServices.Infrastructure.Ovh;

// OVHcloud API v1 wire models. Property matching is case-insensitive in OvhApiClient.

internal sealed record OvhFlavor(
    string Id,
    string Name,
    int Vcpus,
    int Ram,
    int Disk,
    string? Type,
    string? Architecture,
    OvhFlavorPlanCodes? PlanCodes,
    bool? Available);

internal sealed record OvhFlavorPlanCodes(string? Hourly, string? Monthly, string? License);

internal sealed record OvhImage(string Id, string Name, string? OsType, string? Region, string? Status);

internal sealed record OvhIpAddress(string Ip, string? Type, long? Version);

internal sealed record OvhInstance(
    string Id,
    string Name,
    string Status,
    string? Region,
    OvhFlavor? Flavor,
    OvhImage? Image,
    List<OvhIpAddress>? IpAddresses,
    string? ImageId,
    string? ImageName,
    string? PlanCode);

internal sealed record OvhCreateSshKey(string Name, string PublicKey);

internal sealed record OvhSshKey(string Id);

internal sealed record OvhCreateInstance(
    string Name,
    string FlavorId,
    string ImageId,
    string Region,
    string SshKeyId,
    string? UserData,
    bool MonthlyBilling = false);

internal sealed record OvhInstanceReboot(string Type);

internal sealed record OvhDnsZone(string Name, string? Status, List<string>? NameServers, int? RecordCount);

internal sealed record OvhDnsRecord(
    long Id,
    string FieldType,
    string? SubDomain,
    string Target,
    int? Ttl,
    string? Zone);

internal sealed record OvhDnsRecordBody(string FieldType, string SubDomain, string Target, int? Ttl);
