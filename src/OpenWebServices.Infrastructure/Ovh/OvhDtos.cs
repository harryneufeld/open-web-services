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

internal sealed record OvhIpAddress(string Ip, string? Type, long? Version, string? NetworkId = null);

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

// Domain orders (order/cart). Prices are {value, currencyCode, text} in major currency units.

internal sealed record OvhCreateCart(string OvhSubsidiary, string Description);

internal sealed record OvhCart(string CartId);

internal sealed record OvhPrice(decimal Value, string? CurrencyCode, string? Text);

internal sealed record OvhLabeledPrice(string Label, OvhPrice Price);

internal sealed record OvhDomainOffer(string Action, bool Orderable, string? PricingMode, List<OvhLabeledPrice>? Prices);

internal sealed record OvhAddDomain(string Domain, string Duration, string? PricingMode);

internal sealed record OvhCartItem(long ItemId);

internal sealed record OvhRequiredConfiguration(string Label, string? Type, bool? Required);

internal sealed record OvhItemConfiguration(string Label, string Value);

internal sealed record OvhCheckout(bool AutoPayWithPreferredPaymentMethod, bool WaiveRetractationPeriod);

internal sealed record OvhOrderPrices(OvhPrice? WithTax, OvhPrice? WithoutTax);

internal sealed record OvhOrderDetail(string? Description, string? Domain, OvhPrice? TotalPrice);

internal sealed record OvhContract(string Name, string Url);

internal sealed record OvhOrder(long? OrderId, string? Url, OvhOrderPrices? Prices, List<OvhOrderDetail>? Details, List<OvhContract>? Contracts);

// Networking: reverse DNS lives under /ip, private networks (vRack) under the project.

internal sealed record OvhReverseIp(string IpReverse, string Reverse);

internal sealed record OvhSetReverse(string IpReverse, string Reverse);

internal sealed record OvhNetworkRegion(string Region, string? Status, string? OpenstackId);

internal sealed record OvhPrivateNetwork(string Id, string Name, string? Status, string? Type, long? VlanId, List<OvhNetworkRegion>? Regions);

internal sealed record OvhCreatePrivateNetwork(string Name, List<string> Regions);

internal sealed record OvhAddNetworkRegion(string Region);

internal sealed record OvhIpPool(string? Network, string? Region, string? Start, string? End);

internal sealed record OvhSubnet(string Id, string? Cidr, List<OvhIpPool>? IpPools);

internal sealed record OvhCreateSubnet(string Network, string Region, string Start, string End, bool Dhcp, bool NoGateway);

internal sealed record OvhInterfaceFixedIp(string Ip, string? SubnetId);

internal sealed record OvhInterface(string Id, string NetworkId, string? Type, List<OvhInterfaceFixedIp>? FixedIps);

internal sealed record OvhCreateInterface(string NetworkId);

// Edge Network Firewall (/ip/{block}/firewall): stateless, IPv4 only, 20 rules (sequence 0-19), first match wins.

internal sealed record OvhFirewallIp(string IpOnFirewall, bool Enabled, string? State);

internal sealed record OvhAddFirewallIp(string IpOnFirewall);

internal sealed record OvhEnableFirewall(bool Enabled);

internal sealed record OvhFirewallRule(
    long Sequence,
    string Action,
    string Protocol,
    string? Source,
    string? DestinationPort,
    string? SourcePort,
    string? TcpOption,
    string? State);

internal sealed record OvhRange(long From, long To);

internal sealed record OvhTcpOption(string Option);

internal sealed record OvhCreateFirewallRule(
    string Sequence,
    string Action,
    string Protocol,
    string? Source = null,
    long? DestinationPort = null,
    OvhRange? DestinationPortRange = null,
    long? SourcePort = null,
    OvhTcpOption? TcpOption = null);
