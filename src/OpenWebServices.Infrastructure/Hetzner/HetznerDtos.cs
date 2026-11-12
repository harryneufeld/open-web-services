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

internal sealed record HetznerLocation(string Name, string City, string Country);

internal sealed record HetznerDatacenterTypes(List<long> Available);

internal sealed record HetznerDatacenter(HetznerLocation Location, HetznerDatacenterTypes ServerTypes);

internal sealed record HetznerIpv4(string Ip);

internal sealed record HetznerPublicNet(HetznerIpv4? Ipv4);

internal sealed record HetznerServer(long Id, string Status, HetznerPublicNet? PublicNet);

internal sealed record HetznerServerEnvelope(HetznerServer Server);

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
