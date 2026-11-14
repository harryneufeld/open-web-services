using System.Collections.Concurrent;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud Public Cloud server operations through the API v1.</summary>
internal sealed class OvhServerProvider(OvhApiClient api, string projectId, string subsidiary, string endpoint) : IServerProvider
{
    private const decimal EuroSubunits = 100_000_000m;
    private const decimal HoursPerMonth = 730m;
    private static readonly ConcurrentDictionary<string, CachedPriceList> PriceCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct)
    {
        var prices = await GetHourlyPricesAsync(ct);
        // OVH's v1 endpoint returns region names as strings, not region objects.
        var regions = await api.GetAllAsync<string>($"cloud/project/{Path(projectId)}/region", ct);
        var offers = new List<ServerOffer>();
        foreach (var region in regions)
        {
            var flavors = await api.GetAllAsync<OvhFlavor>(
                $"cloud/project/{Path(projectId)}/flavor?region={Query(region)}", ct);
            offers.AddRange(flavors
                .Where(f => f.Available is not false)
                .Select(flavor => ToOffer(region, flavor, prices)));
        }

        return offers;
    }

    public async Task<IReadOnlyList<ProviderServer>> ListServersAsync(CancellationToken ct)
    {
        var prices = await GetHourlyPricesAsync(ct);
        var instances = await api.GetAllAsync<OvhInstance>($"cloud/project/{Path(projectId)}/instance", ct);
        return [.. instances.Select(instance => ToProviderServer(instance, prices))];
    }

    public async Task<ProviderServer?> GetServerAsync(string externalId, CancellationToken ct)
    {
        var instance = await api.GetAsync<OvhInstance>(
            $"cloud/project/{Path(projectId)}/instance/{Path(externalId)}", ct, allowNotFound: true);
        if (instance is null)
            return null;
        return ToProviderServer(instance, await GetHourlyPricesAsync(ct));
    }

    public async Task<string> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct)
    {
        var key = await api.PostAsync<OvhSshKey>(
            $"cloud/project/{Path(projectId)}/sshkey", new OvhCreateSshKey(name, publicKey), ct);
        return key.Id;
    }

    public async Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct)
    {
        var flavors = await api.GetAllAsync<OvhFlavor>(
            $"cloud/project/{Path(projectId)}/flavor?region={Query(request.Location)}", ct);
        var flavor = flavors.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, request.TypeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProviderException($"OVHcloud server flavor {request.TypeName} is unavailable in region {request.Location}.");
        var image = await FindImageAsync(request.Location, request.Image, ct);
        var created = await api.PostAsync<OvhInstance>(
            $"cloud/project/{Path(projectId)}/instance",
            new OvhCreateInstance(request.Name, flavor.Id, image.Id, request.Location, request.SshKeyId, request.UserData),
            ct);
        return new CreatedServer(created.Id, PublicIpv4(created));
    }

    public Task PowerAsync(string externalId, PowerAction action, CancellationToken ct)
    {
        var instancePath = $"cloud/project/{Path(projectId)}/instance/{Path(externalId)}";
        return action switch
        {
            PowerAction.PowerOn => api.PostAsync($"{instancePath}/start", ct),
            PowerAction.Shutdown => api.PostAsync($"{instancePath}/stop", ct),
            PowerAction.Reboot => api.PostAsync($"{instancePath}/reboot", new OvhInstanceReboot("soft"), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    public Task DeleteServerAsync(string externalId, CancellationToken ct) =>
        api.DeleteAsync($"cloud/project/{Path(projectId)}/instance/{Path(externalId)}", ct);

    public Task DeleteSshKeyAsync(string externalSshKeyId, CancellationToken ct) =>
        api.DeleteAsync($"cloud/project/{Path(projectId)}/sshkey/{Path(externalSshKeyId)}", ct);

    private async Task<OvhPriceList> GetHourlyPricesAsync(CancellationToken ct)
    {
        // The public order catalog changes infrequently; cache by API region and subsidiary for a short period.
        // OVH's catalog expresses prices in 1e-8 currency units and hourly plans are billed per started hour.
        var cacheKey = $"{endpoint}:{subsidiary}";
        if (PriceCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Prices;

        var catalog = await api.GetPublicAsync<OvhCatalog>(
            $"order/catalog/public/cloud?ovhSubsidiary={subsidiary}", ct);
        var prices = OvhPriceList.FromCatalog(catalog);
        PriceCache[cacheKey] = new CachedPriceList(DateTimeOffset.UtcNow.AddMinutes(30), prices);
        return prices;
    }

    private async Task<OvhImage> FindImageAsync(string region, string requested, CancellationToken ct)
    {
        var images = await api.GetAllAsync<OvhImage>(
            $"cloud/project/{Path(projectId)}/image?region={Query(region)}", ct);
        var normalized = NormalizeImage(requested);
        return images.FirstOrDefault(image => NormalizeImage(image.Name).Contains(normalized, StringComparison.Ordinal))
               ?? images.FirstOrDefault(image => NormalizeImage(image.OsType ?? "").Contains(normalized, StringComparison.Ordinal))
               ?? throw new ProviderException($"OVHcloud has no {requested} image available in region {region}.");
    }

    private static ServerOffer ToOffer(string region, OvhFlavor flavor, OvhPriceList prices)
    {
        var hourly = prices.Find(flavor.PlanCodes?.Hourly, flavor.Name);
        return new ServerOffer(
            OvhConnector.ProviderType,
            region,
            region,
            "",
            flavor.Name,
            flavor.Vcpus,
            flavor.Type ?? "OVHcloud Public Cloud",
            flavor.Architecture ?? "x86_64",
            flavor.Ram,
            flavor.Disk,
            hourly.Net * HoursPerMonth,
            hourly.Gross * HoursPerMonth,
            prices.Currency,
            Available: true);
    }

    private static ProviderServer ToProviderServer(OvhInstance instance, OvhPriceList prices)
    {
        var flavor = instance.Flavor;
        var price = prices.Find(instance.PlanCode ?? flavor?.PlanCodes?.Hourly, flavor?.Name);
        return new ProviderServer(
            instance.Id,
            instance.Name,
            MapStatus(instance.Status),
            PublicIpv4(instance),
            instance.Region ?? "unknown",
            flavor?.Name ?? "unknown",
            instance.Image?.Name ?? instance.ImageName ?? instance.ImageId ?? "unknown",
            price.Net * HoursPerMonth,
            prices.Currency);
    }

    private static string? PublicIpv4(OvhInstance instance) =>
        instance.IpAddresses?.FirstOrDefault(ip => ip.Version == 4 || ip.Ip.Contains('.'))?.Ip;

    private static ObservedServerState MapStatus(string status) => status.ToUpperInvariant() switch
    {
        "ACTIVE" or "RUNNING" => ObservedServerState.Running,
        "STOPPED" or "SHUTOFF" => ObservedServerState.Stopped,
        "BUILD" or "REBOOT" or "REBOOTING" or "RESIZE" or "RESCUE" => ObservedServerState.Starting,
        "DELETING" or "STOPPING" => ObservedServerState.Stopping,
        _ => ObservedServerState.Unknown,
    };

    private static string NormalizeImage(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Path(string value) => Uri.EscapeDataString(value);
    private static string Query(string value) => Uri.EscapeDataString(value);

    private sealed record CachedPriceList(DateTimeOffset ExpiresAt, OvhPriceList Prices);

    private sealed record OvhCatalog(string CurrencyCode, List<OvhCatalogAddon> Addons);
    private sealed record OvhCatalogAddon(string PlanCode, List<OvhCatalogPricing> Pricings);
    private sealed record OvhCatalogPricing(string IntervalUnit, decimal Price, decimal Tax);
    private sealed record OvhMoney(decimal Net, decimal Gross);

    private sealed class OvhPriceList
    {
        private readonly Dictionary<string, OvhMoney> _hourly;

        private OvhPriceList(string currency, Dictionary<string, OvhMoney> hourly) =>
            (Currency, _hourly) = (currency, hourly);

        public string Currency { get; }

        public OvhMoney Find(string? planCode, string? flavorName)
        {
            var candidates = new[] { planCode, flavorName is null ? null : $"{flavorName}.consumption" }
                .Where(code => !string.IsNullOrWhiteSpace(code));
            foreach (var code in candidates)
                if (_hourly.TryGetValue(code!, out var money))
                    return money;
            return new OvhMoney(0m, 0m);
        }

        public static OvhPriceList FromCatalog(OvhCatalog catalog)
        {
            var prices = new Dictionary<string, OvhMoney>(StringComparer.OrdinalIgnoreCase);
            foreach (var addon in catalog.Addons)
            {
                var hourly = addon.Pricings.FirstOrDefault(price => price.IntervalUnit == "hour");
                if (hourly is not null)
                    prices[addon.PlanCode] = new OvhMoney(hourly.Price / EuroSubunits, (hourly.Price + hourly.Tax) / EuroSubunits);
            }

            return new OvhPriceList(catalog.CurrencyCode, prices);
        }
    }
}
