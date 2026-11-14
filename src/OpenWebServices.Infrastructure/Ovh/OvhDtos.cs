namespace OpenWebServices.Infrastructure.Ovh;

// OVHcloud API v1 wire models (schema: https://eu.api.ovh.com/1.0/cloud.json). Property matching is case-insensitive.

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

internal sealed record OvhFlavorPlanCodes(string? Hourly, string? Monthly);

internal sealed record OvhImage(string Id, string Name, string? Status);

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

// Public order catalog (order/catalog/public/cloud). Prices are in 1e-8 units of the locale currency.

internal sealed record OvhCatalog(OvhCatalogLocale Locale, List<OvhCatalogAddon> Addons);

internal sealed record OvhCatalogLocale(string CurrencyCode);

internal sealed record OvhCatalogAddon(string PlanCode, List<OvhCatalogPricing> Pricings);

internal sealed record OvhCatalogPricing(string IntervalUnit, decimal Price, decimal Tax);

internal sealed record OvhDnsZone(string Name, string? Status, List<string>? NameServers, int? RecordCount);

internal sealed record OvhDnsRecord(
    long Id,
    string FieldType,
    string? SubDomain,
    string Target,
    int? Ttl,
    string? Zone);

internal sealed record OvhDnsRecordBody(string FieldType, string SubDomain, string Target, int? Ttl);
