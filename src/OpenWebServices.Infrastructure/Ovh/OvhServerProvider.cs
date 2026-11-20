using System.Collections.Concurrent;
using System.Net;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud Public Cloud server operations through the API v1.</summary>
internal sealed class OvhServerProvider(OvhApiClient api, string projectId, string subsidiary, string endpoint)
    : IServerProvider, IServerRescaleProvider
{
    /// <summary>The public catalog expresses prices in 1e-8 units of its currency.</summary>
    internal const decimal CatalogPriceScale = 100_000_000m;
    internal const decimal HoursPerMonth = 730m;
    private const int MaxConcurrentRequests = 6;
    // Object-storage-only regions (e.g. "BHS") are listed for the project too; their flavor list answers 404.
    private static readonly HttpStatusCode[] NoComputeInRegion = [HttpStatusCode.NotFound];
    private static readonly OfferBilling Monthly = new(
        "monthly",
        "Monthly",
        "OVHcloud monthly billing: a lower flat rate per calendar month instead of hourly billing. A started month " +
        "is charged in full, and the server cannot be switched back to hourly billing.");

    private static readonly TimeSpan PriceCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, CachedPriceList> PriceCache = new(StringComparer.OrdinalIgnoreCase);

    private string ProjectPath => $"cloud/project/{Escape(projectId)}";

    public async Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct)
    {
        var prices = await GetPricesAsync(ct);
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
        var prices = await GetPricesAsync(ct);
        var instances = await api.GetAllAsync<OvhInstance>($"{ProjectPath}/instance", ct);
        return [.. instances.Select(instance => ToProviderServer(instance, prices))];
    }

    public async Task<ProviderServer?> GetServerAsync(string externalId, CancellationToken ct)
    {
        var instance = await api.GetAsync<OvhInstance>(InstancePath(externalId), ct, allowNotFound: true);
        return instance is null ? null : ToProviderServer(instance, await GetPricesAsync(ct));
    }

    public async Task<string> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct)
    {
        var key = await api.PostAsync<OvhSshKey>($"{ProjectPath}/sshkey", new OvhCreateSshKey(name, publicKey), ct);
        return key.Id;
    }

    public async Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct)
    {
        if (request.BillingId is not null && request.BillingId != Monthly.Id)
            throw new ProviderException($"OVHcloud does not offer the billing option '{request.BillingId}'.");
        var flavor = (await GetFlavorsAsync(request.Location, ct)).FirstOrDefault(candidate =>
                         string.Equals(candidate.Name, request.TypeName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ProviderException($"OVHcloud server flavor {request.TypeName} is unavailable in region {request.Location}.");
        var image = await FindImageAsync(request.Location, request.Image, ct);
        var created = await api.PostAsync<OvhInstance>(
            $"{ProjectPath}/instance",
            new OvhCreateInstance(request.Name, flavor.Id, image.Id, request.Location, request.SshKeyId, request.UserData,
                MonthlyBilling: request.BillingId == Monthly.Id),
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

    /// <summary>
    /// OVHcloud resizes running instances in place and restarts them. Disks never shrink, so targets are flavors of the
    /// same family (Linux or Windows, flex or not) and architecture with at least as much disk; monthly-billed
    /// instances stay monthly.
    /// </summary>
    public async Task<RescaleOptions> GetRescaleOptionsAsync(string externalServerId, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(externalServerId, ct);
        var region = instance.Region ?? throw new ProviderException("OVHcloud does not say where the server is.");
        var flavors = await GetFlavorsAsync(region, ct);
        var current = flavors.FirstOrDefault(f => f.Id == instance.Flavor?.Id || f.Name == instance.Flavor?.Name) ?? instance.Flavor
                      ?? throw new ProviderException("OVHcloud does not say which flavor the server has.");
        var prices = await GetPricesAsync(ct);
        var monthly = IsMonthly(instance, current);

        ServerOffer? Offer(OvhFlavor flavor, bool available)
        {
            var price = monthly
                ? prices.Find(flavor.PlanCodes?.Monthly ?? $"{flavor.Name}.monthly.postpaid")
                : prices.Find(flavor.PlanCodes?.Hourly ?? $"{flavor.Name}.consumption");
            return price is null
                ? null
                : ToOffer(region, flavor, price, prices.Currency) with { Available = available, Billing = monthly ? Monthly : null };
        }

        var targets = flavors
            .Where(f => f.Name != current.Name && f.Disk >= current.Disk && SameFamily(f, current))
            .Select(f => Offer(f, f.Available is not false))
            .OfType<ServerOffer>()
            .OrderBy(o => o.MonthlyNet)
            .ThenBy(o => o.TypeName, StringComparer.Ordinal)
            .ToList();

        return new RescaleOptions(
            Offer(current, available: true),
            current.Disk,
            targets,
            StopsServer: false,
            DiskIsOptional: false,
            "OVHcloud restarts the server to resize it, which usually takes a few minutes. The disk grows to the new " +
            "flavor's size and never shrinks, so the server cannot move back to a flavor with a smaller disk.");
    }

    public async Task RescaleAsync(string externalServerId, string typeName, bool growDisk, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(externalServerId, ct);
        var region = instance.Region ?? throw new ProviderException("OVHcloud does not say where the server is.");
        var flavor = (await GetFlavorsAsync(region, ct)).FirstOrDefault(f => string.Equals(f.Name, typeName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ProviderException($"OVHcloud server flavor {typeName} is unavailable in region {region}.");
        await api.PostAsync($"{InstancePath(externalServerId)}/resize", new OvhResizeInstance(flavor.Id), ct);
    }

    private async Task<OvhInstance> GetInstanceAsync(string externalServerId, CancellationToken ct) =>
        await api.GetAsync<OvhInstance>(InstancePath(externalServerId), ct, allowNotFound: true)
        ?? throw new NotFoundException("Server at OVHcloud");

    private static bool IsMonthly(OvhInstance instance, OvhFlavor flavor) =>
        instance.PlanCode is { } planCode
        && (string.Equals(planCode, flavor.PlanCodes?.Monthly, StringComparison.OrdinalIgnoreCase)
            || planCode.Contains(".monthly", StringComparison.OrdinalIgnoreCase));

    /// <summary>OVHcloud resizes within Linux or Windows flavors, flex or not, of one architecture.</summary>
    internal static bool SameFamily(OvhFlavor candidate, OvhFlavor current) =>
        IsWindows(candidate) == IsWindows(current)
        && IsFlex(candidate) == IsFlex(current)
        && NormalizeArchitecture(candidate.Architecture) == NormalizeArchitecture(current.Architecture);

    private static bool IsWindows(OvhFlavor flavor) => flavor.Name.StartsWith("win-", StringComparison.OrdinalIgnoreCase);

    private static bool IsFlex(OvhFlavor flavor) => flavor.Name.EndsWith("-flex", StringComparison.OrdinalIgnoreCase);

    private string InstancePath(string externalId) => $"{ProjectPath}/instance/{Escape(externalId)}";

    private async Task<IReadOnlyList<ServerOffer>> GetRegionOffersAsync(string regionName, OvhPriceList prices, CancellationToken ct)
    {
        var flavors = await api.GetAsync<List<OvhFlavor>>(
            $"{ProjectPath}/flavor?region={Escape(regionName)}", ct, NoComputeInRegion);
        return [.. (flavors ?? [])
            .Where(flavor => flavor.Available is not false)
            .SelectMany(flavor => ToOffers(regionName, flavor, prices))];
    }

    private Task<List<OvhFlavor>> GetFlavorsAsync(string region, CancellationToken ct) =>
        api.GetAllAsync<OvhFlavor>($"{ProjectPath}/flavor?region={Escape(region)}", ct);

    private async Task<OvhPriceList> GetPricesAsync(CancellationToken ct)
    {
        // The public catalog changes rarely and is the same for every project, so it is shared per API endpoint and
        // subsidiary for a short time.
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

    /// <summary>The pay-as-you-go offer, plus a monthly-billing offer when the catalog prices one.</summary>
    private static IEnumerable<ServerOffer> ToOffers(string region, OvhFlavor flavor, OvhPriceList prices)
    {
        var hourly = prices.Find(flavor.PlanCodes?.Hourly ?? $"{flavor.Name}.consumption") ?? OvhPriceList.Unknown;
        var offer = ToOffer(region, flavor, hourly, prices.Currency);
        yield return offer;
        if (prices.Find(flavor.PlanCodes?.Monthly ?? $"{flavor.Name}.monthly.postpaid") is { } monthly)
            yield return ToOffer(region, flavor, monthly, prices.Currency) with { Billing = Monthly };
    }

    private static ServerOffer ToOffer(string region, OvhFlavor flavor, Money monthlyPrice, string currency)
    {
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
            monthlyPrice.Net,
            monthlyPrice.Gross,
            currency,
            Available: true)
        {
            Tier = Tier(flavor.Name),
        };
    }

    /// <summary>
    /// The product line follows the flavor's name prefix (b3-8, c3-4, d2-2, win-r2-30, ...). OVHcloud's Public Cloud
    /// SLA promises 99.99 % monthly availability for instances and 99.95 % for Discovery instances; GPU and older
    /// lines are left without a number rather than guessed.
    /// </summary>
    internal static OfferTier Tier(string flavorName)
    {
        var name = flavorName.ToLowerInvariant();
        if (name.StartsWith("win-", StringComparison.Ordinal))
            name = name[4..];
        var prefix = new string(name.TakeWhile(char.IsAsciiLetterLower).ToArray());
        return prefix switch
        {
            "d" => new OfferTier(ServerCategory.SharedBudget, "Discovery", 99.95m),
            "b" => new OfferTier(ServerCategory.Dedicated, "General Purpose", 99.99m),
            "c" => new OfferTier(ServerCategory.ComputeOptimized, "Compute Optimized", 99.99m),
            "r" => new OfferTier(ServerCategory.MemoryOptimized, "Memory Optimized", 99.99m),
            "i" => new OfferTier(ServerCategory.StorageOptimized, "Storage Optimized", 99.99m),
            "t" or "l" or "h" or "a" or "rtx" => new OfferTier(ServerCategory.Gpu, "Cloud GPU", null),
            "s" => new OfferTier(ServerCategory.SharedBudget, "Sandbox", null),
            _ => new OfferTier(ServerCategory.Other, prefix.ToUpperInvariant(), null),
        };
    }

    private static ProviderServer ToProviderServer(OvhInstance instance, OvhPriceList prices)
    {
        var flavor = instance.Flavor;
        // Monthly-billed instances report their monthly plan code, so the price follows the instance's billing.
        var price = prices.Find(instance.PlanCode)
                    ?? prices.Find(flavor?.PlanCodes?.Hourly ?? $"{flavor?.Name}.consumption")
                    ?? OvhPriceList.Unknown;
        return new ProviderServer(
            instance.Id,
            instance.Name,
            MapStatus(instance.Status),
            PublicIpv4(instance),
            instance.Region ?? "unknown",
            flavor?.Name ?? "unknown",
            instance.Image?.Name ?? instance.ImageId ?? "unknown",
            price.Net,
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
        "BUILD" or "BUILDING" or "REBOOT" or "HARD_REBOOT" or "REBUILD" or "RESIZE" or "VERIFY_RESIZE" or "RESUMING" or "UNSHELVING"
            or "MIGRATING" => ObservedServerState.Starting,
        "DELETING" or "SHELVING" => ObservedServerState.Stopping,
        _ => ObservedServerState.Unknown,
    };

    private static string NormalizeImage(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record CachedPriceList(DateTimeOffset ExpiresAt, OvhPriceList Prices);

    private sealed record Money(decimal Net, decimal Gross);

    /// <summary>Catalog prices per plan code, as a monthly amount: hourly plans × 730 h, monthly plans as listed.</summary>
    private sealed class OvhPriceList(string currency, Dictionary<string, Money> perMonth)
    {
        public static readonly Money Unknown = new(0m, 0m);

        public string Currency => currency;

        public Money? Find(string? planCode) =>
            !string.IsNullOrWhiteSpace(planCode) && perMonth.TryGetValue(planCode, out var money) ? money : null;

        public static OvhPriceList FromCatalog(OvhCatalog catalog)
        {
            var prices = new Dictionary<string, Money>(StringComparer.OrdinalIgnoreCase);
            foreach (var addon in catalog.Addons)
            {
                if (addon.Pricings.FirstOrDefault(pricing => pricing.IntervalUnit == "hour") is { } hourly)
                    prices[addon.PlanCode] = ToMoney(hourly, HoursPerMonth);
                else if (addon.Pricings.FirstOrDefault(pricing => pricing.IntervalUnit == "month") is { } monthly)
                    prices[addon.PlanCode] = ToMoney(monthly, 1m);
            }

            return new OvhPriceList(catalog.Locale.CurrencyCode, prices);
        }

        private static Money ToMoney(OvhCatalogPricing pricing, decimal factor) =>
            new(pricing.Price / CatalogPriceScale * factor, (pricing.Price + pricing.Tax) / CatalogPriceScale * factor);
    }
}
