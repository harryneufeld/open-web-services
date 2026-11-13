using System.Globalization;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Hetzner;

internal sealed class HetznerServerProvider(HetznerApiClient api) : IServerProvider
{
    private const string Currency = "EUR";

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

    public async Task<IReadOnlyList<ProviderServer>> ListServersAsync(CancellationToken ct) =>
        [.. (await api.GetAllAsync<HetznerServer>("servers", "servers", ct)).Select(ToProviderServer)];

    public async Task<ProviderServer?> GetServerAsync(string externalId, CancellationToken ct)
    {
        var envelope = await api.GetAsync<HetznerServerEnvelope>($"servers/{ParseId(externalId)}", ct);
        return envelope is null ? null : ToProviderServer(envelope.Server);
    }

    public async Task<string> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct)
    {
        var created = await api.PostAsync<HetznerSshKeyEnvelope>(
            "ssh_keys", new HetznerCreateSshKeyBody(name, publicKey), ct);
        return ToExternalId(created.SshKey.Id);
    }

    public async Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct)
    {
        var body = new HetznerCreateServerBody(
            request.Name, request.TypeName, request.Location, request.Image,
            [ParseId(request.SshKeyId)], request.UserData, StartAfterCreate: true);
        var created = await api.PostAsync<HetznerServerEnvelope>("servers", body, ct);
        return new CreatedServer(ToExternalId(created.Server.Id), created.Server.PublicNet?.Ipv4?.Ip);
    }

    public Task PowerAsync(string externalId, PowerAction action, CancellationToken ct) =>
        api.PostAsync($"servers/{ParseId(externalId)}/actions/{ToActionName(action)}", ct);

    public Task DeleteServerAsync(string externalId, CancellationToken ct) =>
        api.DeleteAsync($"servers/{ParseId(externalId)}", ct);

    public Task DeleteSshKeyAsync(string externalSshKeyId, CancellationToken ct) =>
        api.DeleteAsync($"ssh_keys/{ParseId(externalSshKeyId)}", ct);

    private static ProviderServer ToProviderServer(HetznerServer server)
    {
        var location = server.Location?.Name ?? "";
        var price = server.ServerType?.Prices.FirstOrDefault(p => p.Location == location)?.PriceMonthly.Net ?? 0m;
        return new ProviderServer(
            ToExternalId(server.Id),
            server.Name,
            MapStatus(server.Status),
            server.PublicNet?.Ipv4?.Ip,
            location.Length > 0 ? location : "unknown",
            server.ServerType?.Name ?? "unknown",
            server.Image?.Name ?? server.Image?.Description ?? "unknown",
            price,
            Currency);
    }

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
            HetznerConnector.ProviderType, price.Location, location?.City ?? price.Location, location?.Country ?? "",
            type.Name, type.Cores, type.CpuType, type.Architecture, type.Memory, type.Disk,
            price.PriceMonthly.Net, price.PriceMonthly.Gross, Currency, available);
    }

    private static bool IsDeprecatedAt(HetznerServerType type, string location) =>
        type.Locations?.Any(l => l.Name == location && l.Deprecation is not null) ?? false;

    internal static ObservedServerState MapStatus(string status) => status switch
    {
        "running" => ObservedServerState.Running,
        "off" => ObservedServerState.Stopped,
        "initializing" or "starting" or "migrating" or "rebuilding" => ObservedServerState.Starting,
        "stopping" or "deleting" => ObservedServerState.Stopping,
        _ => ObservedServerState.Unknown,
    };

    private static string ToActionName(PowerAction action) => action switch
    {
        PowerAction.PowerOn => "poweron",
        PowerAction.Shutdown => "shutdown",
        PowerAction.Reboot => "reboot",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static string ToExternalId(long id) => id.ToString(CultureInfo.InvariantCulture);

    internal static long ParseId(string externalId) =>
        long.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ProviderException($"'{externalId}' is not a valid Hetzner resource id.");
}
