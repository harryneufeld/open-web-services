using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Hetzner;

internal sealed class HetznerProvider(HetznerApiClient api) : IHostingProvider
{
    public const string ProviderId = "hetzner";
    private const string Currency = "EUR";

    public string Id => ProviderId;

    public async Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct)
    {
        var types = await api.GetAllAsync<HetznerServerType>("server_types", "server_types", ct);
        var locations = (await api.GetAllAsync<HetznerLocation>("locations", "locations", ct))
            .ToDictionary(l => l.Name);
        var availability = await LoadAvailabilityAsync(ct);

        return
        [
            .. from type in types
               where type.Deprecation is null
               from price in type.Prices
               where !IsDeprecatedAt(type, price.Location)
               select ToOffer(type, price, locations, availability),
        ];
    }

    public async Task<long> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct)
    {
        var created = await api.PostAsync<HetznerSshKeyEnvelope>(
            "ssh_keys", new HetznerCreateSshKeyBody(name, publicKey), ct);
        return created.SshKey.Id;
    }

    public async Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct)
    {
        var body = new HetznerCreateServerBody(
            request.Name, request.TypeName, request.Location, request.Image,
            [request.SshKeyId], request.UserData, StartAfterCreate: true);
        var created = await api.PostAsync<HetznerServerEnvelope>("servers", body, ct);
        return new CreatedServer(created.Server.Id, created.Server.PublicNet?.Ipv4?.Ip);
    }

    public async Task<ProviderServerState?> GetServerAsync(long providerServerId, CancellationToken ct)
    {
        var envelope = await api.GetAsync<HetznerServerEnvelope>($"servers/{providerServerId}", ct);
        return envelope is null
            ? null
            : new ProviderServerState(MapStatus(envelope.Server.Status), envelope.Server.PublicNet?.Ipv4?.Ip);
    }

    public Task PowerAsync(long providerServerId, PowerAction action, CancellationToken ct) =>
        api.PostAsync($"servers/{providerServerId}/actions/{ToActionName(action)}", ct);

    public Task DeleteServerAsync(long providerServerId, CancellationToken ct) =>
        api.DeleteAsync($"servers/{providerServerId}", ct);

    public Task DeleteSshKeyAsync(long providerSshKeyId, CancellationToken ct) =>
        api.DeleteAsync($"ssh_keys/{providerSshKeyId}", ct);

    /// <summary>Location name -> ids of server types that can be ordered there right now. Null if unknown.</summary>
    private async Task<Dictionary<string, HashSet<long>>?> LoadAvailabilityAsync(CancellationToken ct)
    {
        try
        {
            var datacenters = await api.GetAllAsync<HetznerDatacenter>("datacenters", "datacenters", ct);
            return datacenters
                .GroupBy(d => d.Location.Name)
                .ToDictionary(g => g.Key, g => g.SelectMany(d => d.ServerTypes.Available).ToHashSet());
        }
        catch (ProviderException)
        {
            return null; // availability unknown: show offers as orderable, creation will fail loudly if not
        }
    }

    private static ServerOffer ToOffer(
        HetznerServerType type,
        HetznerPrice price,
        Dictionary<string, HetznerLocation> locations,
        Dictionary<string, HashSet<long>>? availability)
    {
        locations.TryGetValue(price.Location, out var location);
        var available = availability is null
                        || (availability.TryGetValue(price.Location, out var ids) && ids.Contains(type.Id));

        return new ServerOffer(
            ProviderId, price.Location, location?.City ?? price.Location, location?.Country ?? "",
            type.Name, type.Cores, type.CpuType, type.Architecture, type.Memory, type.Disk,
            price.PriceMonthly.Net, price.PriceMonthly.Gross, Currency, available);
    }

    private static bool IsDeprecatedAt(HetznerServerType type, string location) =>
        type.Locations?.Any(l => l.Name == location && l.Deprecation is not null) ?? false;

    private static ProviderServerStatus MapStatus(string status) => status switch
    {
        "running" => ProviderServerStatus.Running,
        "off" => ProviderServerStatus.Stopped,
        "initializing" or "starting" or "migrating" or "rebuilding" => ProviderServerStatus.Starting,
        _ => ProviderServerStatus.Unknown,
    };

    private static string ToActionName(PowerAction action) => action switch
    {
        PowerAction.PowerOn => "poweron",
        PowerAction.Shutdown => "shutdown",
        PowerAction.Reboot => "reboot",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
