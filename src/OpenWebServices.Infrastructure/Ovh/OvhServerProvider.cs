using System.Collections.Concurrent;
using System.Net;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud Public Cloud server operations through the API v1.</summary>
internal sealed class OvhServerProvider(OvhApiClient api, string projectId, string subsidiary, string endpoint) : IServerProvider
{
    /// <summary>The public catalog expresses prices in 1e-8 units of its currency.</summary>
    private const decimal CatalogPriceScale = 100_000_000m;
    private const decimal HoursPerMonth = 730m;
    private const int MaxConcurrentRequests = 6;
    // Object-storage-only regions (e.g. "BHS") are listed for the project too; their flavor list answers 404.
    private static readonly HttpStatusCode[] NoComputeInRegion = [HttpStatusCode.NotFound];
    private static readonly TimeSpan PriceCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, CachedPriceList> PriceCache = new(StringComparer.OrdinalIgnoreCase);

    private string ProjectPath => $"cloud/project/{Escape(projectId)}";

    public async Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct)
    {
        var prices = await GetHourlyPricesAsync(ct);
        // Only the region list and flavor endpoints are used, so a consumer key limited to the calls this
        // provider makes keeps working (region details, for example, are often not granted).
        var regionNames = await api.GetAllAsync<string>($"{ProjectPath}/region", ct);
        var offersByRegion = new IReadOnlyList<ServerOffer>[regionNames.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, regionNames.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentRequests, CancellationToken = ct },
            async (i, token) => offersByRegion[i] = await GetRegionOffersAsync(regionNames[i], prices, token));
        return [.. offersByRegion.SelectMany(offers => offers)];
    }

    public async Task<IReadOnlyList<ProviderServer>> ListServersAsync(CancellationToken ct)
    {
        var prices = await GetHourlyPricesAsync(ct);
        var instances = await api.GetAllAsync<OvhInstance>($"{ProjectPath}/instance", ct);
        return [.. instances.Select(instance => ToProviderServer(instance, prices))];
    }

    public async Task<ProviderServer?> GetServerAsync(string externalId, CancellationToken ct)
    {
        var instance = await api.GetAsync<OvhInstance>(InstancePath(externalId), ct, allowNotFound: true);
        return instance is null ? null : ToProviderServer(instance, await GetHourlyPricesAsync(ct));
    }

    public async Task<string> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct)
    {
        var key = await api.PostAsync<OvhSshKey>($"{ProjectPath}/sshkey", new OvhCreateSshKey(name, publicKey), ct);
        return key.Id;
    }

    public async Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct)
    {
        var flavor = (await GetFlavorsAsync(request.Location, ct)).FirstOrDefault(candidate =>
                         string.Equals(candidate.Name, request.TypeName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ProviderException($"OVHcloud server flavor {request.TypeName} is unavailable in region {request.Location}.");
        var image = await FindImageAsync(request.Location, request.Image, ct);
        var created = await api.PostAsync<OvhInstance>(
            $"{ProjectPath}/instance",
            new OvhCreateInstance(request.Name, flavor.Id, image.Id, request.Location, request.SshKeyId, request.UserData),
            ct);
        return new CreatedServer(created.Id, PublicIpv4(created));
    }

    public Task PowerAsync(string externalId, PowerAction action, CancellationToken ct)
    {
        var instancePath = InstancePath(externalId);
        return action switch
        {
            PowerAction.PowerOn => api.PostAsync($"{instancePath}/start", ct),
            PowerAction.Shutdown => api.PostAsync($"{instancePath}/stop", ct),
            PowerAction.Reboot => api.PostAsync($"{instancePath}/reboot", new OvhInstanceReboot("soft"), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    public Task DeleteServerAsync(string externalId, CancellationToken ct) => api.DeleteAsync(InstancePath(externalId), ct);

    public Task DeleteSshKeyAsync(string externalSshKeyId, CancellationToken ct) =>
        api.DeleteAsync($"{ProjectPath}/sshkey/{Escape(externalSshKeyId)}", ct);

    private string InstancePath(string externalId) => $"{ProjectPath}/instance/{Escape(externalId)}";

    private async Task<IReadOnlyList<ServerOffer>> GetRegionOffersAsync(string regionName, OvhPriceList prices, CancellationToken ct)
    {
        var flavors = await api.GetAsync<List<OvhFlavor>>(
            $"{ProjectPath}/flavor?region={Escape(regionName)}", ct, NoComputeInRegion);
        return [.. (flavors ?? [])
            .Where(flavor => flavor.Available is not false)
            .Select(flavor => ToOffer(regionName, flavor, prices))];
    }

    private Task<List<OvhFlavor>> GetFlavorsAsync(string region, CancellationToken ct) =>
        api.GetAllAsync<OvhFlavor>($"{ProjectPath}/flavor?region={Escape(region)}", ct);

    private async Task<OvhPriceList> GetHourlyPricesAsync(CancellationToken ct)
    {
        // The public catalog changes rarely and is the same for every project, so it is shared per API endpoint and
        // subsidiary for a short time. Hourly plans are billed per started hour.
        var cacheKey = $"{endpoint}:{subsidiary}";
        if (PriceCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Prices;

        var catalog = await api.GetPublicAsync<OvhCatalog>($"order/catalog/public/cloud?ovhSubsidiary={Escape(subsidiary)}", ct);
        var prices = OvhPriceList.FromCatalog(catalog);
        PriceCache[cacheKey] = new CachedPriceList(DateTimeOffset.UtcNow + PriceCacheLifetime, prices);
        return prices;
    }

    private async Task<OvhImage> FindImageAsync(string region, string requested, CancellationToken ct)
    {
        var images = (await api.GetAllAsync<OvhImage>($"{ProjectPath}/image?region={Escape(region)}", ct))
            .Where(image => image.Status is null || image.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var normalized = NormalizeImage(requested);
        // "ubuntu-24.04" matches "Ubuntu 24.04" exactly; variants such as "Ubuntu 24.04 - Docker" are a fallback only.
        return images.FirstOrDefault(image => NormalizeImage(image.Name) == normalized)
               ?? images.FirstOrDefault(image => NormalizeImage(image.Name).StartsWith(normalized, StringComparison.Ordinal))
               ?? throw new ProviderException($"OVHcloud has no {requested} image available in region {region}.");
    }

    private static ServerOffer ToOffer(string region, OvhFlavor flavor, OvhPriceList prices)
    {
        var hourly = prices.Find(flavor.PlanCodes?.Hourly, flavor.Name);
        var (city, country) = OvhRegions.Describe(region);
        return new ServerOffer(
            OvhConnector.ProviderType,
            region,
            city,
            country,
            flavor.Name,
            flavor.Vcpus,
            flavor.Type ?? "OVHcloud Public Cloud",
            NormalizeArchitecture(flavor.Architecture),
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
            instance.Image?.Name ?? instance.ImageId ?? "unknown",
            price.Net * HoursPerMonth,
            prices.Currency);
    }

    /// <summary>Uses the same names as the other providers ("x86", "arm") so offers can be compared and filtered.</summary>
    private static string NormalizeArchitecture(string? architecture) => architecture?.ToLowerInvariant() switch
    {
        null or "" or "x86_64" or "amd64" or "x86" => "x86",
        "arm64" or "aarch64" or "arm" => "arm",
        var other => other,
    };

    private static string? PublicIpv4(OvhInstance instance) =>
        instance.IpAddresses?.FirstOrDefault(ip => ip.Version == 4 && ip.Type == "public")?.Ip;

    // cloud.instance.InstanceStatusEnum
    internal static ObservedServerState MapStatus(string status) => status.ToUpperInvariant() switch
    {
        "ACTIVE" => ObservedServerState.Running,
        "STOPPED" or "SHUTOFF" or "SHELVED" or "SHELVED_OFFLOADED" or "SUSPENDED" or "PAUSED" => ObservedServerState.Stopped,
        "BUILD" or "BUILDING" or "REBOOT" or "HARD_REBOOT" or "REBUILD" or "RESIZE" or "RESUMING" or "UNSHELVING"
            or "MIGRATING" => ObservedServerState.Starting,
        "DELETING" or "SHELVING" => ObservedServerState.Stopping,
        _ => ObservedServerState.Unknown,
    };

    private static string NormalizeImage(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record CachedPriceList(DateTimeOffset ExpiresAt, OvhPriceList Prices);

    private sealed record Money(decimal Net, decimal Gross);

    private sealed class OvhPriceList(string currency, Dictionary<string, Money> hourly)
    {
        private static readonly Money Unknown = new(0m, 0m);

        public string Currency => currency;

        /// <summary>Looks up the plan code, falling back to the flavor's default consumption plan.</summary>
        public Money Find(string? planCode, string? flavorName)
        {
            if (!string.IsNullOrWhiteSpace(planCode) && hourly.TryGetValue(planCode, out var byPlan))
                return byPlan;
            if (flavorName is not null && hourly.TryGetValue($"{flavorName}.consumption", out var byFlavor))
                return byFlavor;
            return Unknown;
        }

        public static OvhPriceList FromCatalog(OvhCatalog catalog)
        {
            var prices = new Dictionary<string, Money>(StringComparer.OrdinalIgnoreCase);
            foreach (var addon in catalog.Addons)
            {
                var price = addon.Pricings.FirstOrDefault(pricing => pricing.IntervalUnit == "hour");
                if (price is not null)
                    prices[addon.PlanCode] = new Money(price.Price / CatalogPriceScale, (price.Price + price.Tax) / CatalogPriceScale);
            }

            return new OvhPriceList(catalog.Locale.CurrencyCode, prices);
        }
    }
}
