using System.Collections.Concurrent;
using System.Net;
using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>
/// OVHcloud Public Cloud block storage (/cloud/project/{id}/volume). Volumes live in one region (and availability
/// zone, in 3-AZ regions), come in several performance types, and are grown while detached. On the instance a volume
/// appears as a virtio disk whose serial is the first 20 characters of its id.
/// </summary>
internal sealed class OvhVolumeProvider(OvhApiClient api, string projectId, string subsidiary, string endpoint) : IVolumeProvider
{
    public const int MinSizeGb = 10;
    public const int MaxSizeGb = 12000;
    private const string Available = "available";
    private const string InUse = "in-use";
    private static readonly HttpStatusCode[] Missing = [HttpStatusCode.NotFound];
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PriceCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, CachedPrices> PriceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The types ows offers, cheapest first, with a short explanation each.</summary>
    private static readonly (string Id, string Label, string Description)[] KnownTypes =
    [
        ("classic", "Classic", "Lowest price, moderate speed: backups, archives, media"),
        ("high-speed", "High speed", "SSD: databases and busy apps"),
        ("high-speed-gen2", "High speed Gen2", "SSD whose speed grows with its size"),
        ("classic-luks", "Classic, encrypted", "Classic, encrypted at rest"),
        ("high-speed-luks", "High speed, encrypted", "High speed, encrypted at rest"),
        ("high-speed-gen2-luks", "High speed Gen2, encrypted", "High speed Gen2, encrypted at rest"),
    ];

    /// <summary>How often a busy volume is polled; tests set it to zero.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    private string ProjectPath => $"cloud/project/{Escape(projectId)}";

    private string VolumePath(string volumeId) => $"{ProjectPath}/volume/{Escape(volumeId)}";

    public async Task<VolumeOptions> GetOptionsAsync(string externalServerId, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(externalServerId, ct);
        var region = instance.Region ?? throw new ProviderException("OVHcloud did not report the server's region.");
        var offered = await api.GetAsync<List<OvhVolumeType>>($"{ProjectPath}/region/{Escape(region)}/volumeType", ct, Missing);
        var names = offered?.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var prices = await GetPricesAsync(ct);

        // Without a list from the region, offer the unencrypted types every region has.
        var types = KnownTypes
            .Where(t => names.Count == 0 ? !t.Id.EndsWith("-luks", StringComparison.Ordinal) : names.Contains(t.Id))
            .Select(t => new VolumeTypeOption(t.Id, t.Label, t.Description, prices.PerGb.TryGetValue(t.Id, out var price) ? price : null))
            .ToList();
        if (types.Count == 0)
            types.Add(new VolumeTypeOption("classic", "Classic", null, prices.PerGb.TryGetValue("classic", out var classic) ? classic : null));
        return new VolumeOptions(region, MinSizeGb, MaxSizeGb, types, prices.Currency, CanChangeType: true, ResizesWhileAttached: false);
    }

    public async Task<IReadOnlyList<ProviderVolume>> ListVolumesAsync(CancellationToken ct) =>
        [.. (await api.GetAllAsync<OvhVolume>($"{ProjectPath}/volume", ct)).Select(ToVolume)];

    public async Task<ProviderVolume> CreateVolumeAsync(CreateVolumeRequest request, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(request.ServerId, ct);
        var region = instance.Region ?? throw new ProviderException("OVHcloud did not report the server's region.");
        var created = await api.PostAsync<OvhVolume>($"{ProjectPath}/volume",
            new OvhCreateVolume(region, request.SizeGb, request.Type ?? "classic", request.Name, instance.AvailabilityZone), ct);
        await WaitForAsync(created.Id, Available, ct);
        await AttachAsync(created.Id, request.ServerId, ct);
        return ToVolume(await GetVolumeAsync(created.Id, ct));
    }

    public async Task AttachAsync(string volumeId, string externalServerId, CancellationToken ct)
    {
        await api.PostAsync($"{VolumePath(volumeId)}/attach", new OvhVolumeInstance(externalServerId), ct);
        await WaitForAsync(volumeId, InUse, ct);
    }

    public async Task DetachAsync(string volumeId, CancellationToken ct)
    {
        var volume = await GetVolumeAsync(volumeId, ct);
        foreach (var instanceId in volume.AttachedTo ?? [])
            await api.PostAsync($"{VolumePath(volumeId)}/detach", new OvhVolumeInstance(instanceId), ct);
        await WaitForAsync(volumeId, Available, ct);
    }

    public Task RenameAsync(string volumeId, string name, CancellationToken ct) =>
        api.PutAsync(VolumePath(volumeId), new OvhVolumeName(name), ct);

    public async Task ResizeAsync(string volumeId, int sizeGb, CancellationToken ct)
    {
        await api.PostAsync($"{VolumePath(volumeId)}/upsize", new OvhVolumeSize(sizeGb), ct);
        await WaitUntilIdleAsync(volumeId, ct);
    }

    public async Task ChangeTypeAsync(string volumeId, string type, CancellationToken ct)
    {
        var volume = await GetVolumeAsync(volumeId, ct);
        await api.PutAsync($"{ProjectPath}/region/{Escape(volume.Region)}/volume/{Escape(volumeId)}", new OvhVolumeRetype(type), ct);
        await WaitUntilIdleAsync(volumeId, ct);
    }

    public Task DeleteAsync(string volumeId, CancellationToken ct) => api.DeleteAsync(VolumePath(volumeId), ct);

    /// <summary>The stable path of a volume on its instance: /dev/disk/by-id/virtio- plus the first 20 characters of its id.</summary>
    internal static string DevicePath(string volumeId) => "/dev/disk/by-id/virtio-" + volumeId[..Math.Min(20, volumeId.Length)];

    private static ProviderVolume ToVolume(OvhVolume volume)
    {
        var status = volume.Status ?? Available;
        return new ProviderVolume(
            volume.Id,
            volume.Name,
            volume.Size,
            volume.Region,
            volume.AttachedTo is { Count: > 0 } attached ? attached[0] : null,
            volume.Type,
            status,
            DevicePath(volume.Id),
            IsBusy: status is not (Available or InUse) && !status.StartsWith("error", StringComparison.Ordinal));
    }

    private Task WaitForAsync(string volumeId, string wanted, CancellationToken ct) =>
        WaitAsync(volumeId, status => status == wanted, ct);

    private Task WaitUntilIdleAsync(string volumeId, CancellationToken ct) =>
        WaitAsync(volumeId, status => status is Available or InUse, ct);

    private async Task WaitAsync(string volumeId, Func<string, bool> done, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BusyTimeout;
        while (true)
        {
            var status = (await GetVolumeAsync(volumeId, ct)).Status ?? Available;
            if (done(status))
                return;
            if (status.StartsWith("error", StringComparison.Ordinal))
                throw new ProviderException($"OVHcloud could not complete the change; the volume reports '{status}'.");
            if (DateTime.UtcNow > deadline)
                throw new ProviderException("OVHcloud is still working on the volume. Reload the page in a moment.");
            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task<OvhVolume> GetVolumeAsync(string volumeId, CancellationToken ct) =>
        await api.GetAsync<OvhVolume>(VolumePath(volumeId), ct, Missing) ?? throw new NotFoundException("Volume at OVHcloud");

    private async Task<OvhInstance> GetInstanceAsync(string externalServerId, CancellationToken ct) =>
        await api.GetAsync<OvhInstance>($"{ProjectPath}/instance/{Escape(externalServerId)}", ct, Missing)
        ?? throw new NotFoundException("Server at OVHcloud");

    /// <summary>Net price per GB and month by type: the catalog's monthly plan, or its hourly plan × 730 h.</summary>
    private async Task<CachedPrices> GetPricesAsync(CancellationToken ct)
    {
        var cacheKey = $"{endpoint}:{subsidiary}";
        if (PriceCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached;

        OvhCatalog catalog;
        try
        {
            catalog = await api.GetPublicAsync<OvhCatalog>($"order/catalog/public/cloud?ovhSubsidiary={Escape(subsidiary)}", ct);
        }
        catch (ProviderException)
        {
            // Prices are a hint; volumes work without them.
            return new CachedPrices(DateTimeOffset.MinValue, "EUR", []);
        }

        var plans = catalog.Addons.GroupBy(a => a.PlanCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Pricings, StringComparer.OrdinalIgnoreCase);
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _, _) in KnownTypes)
        {
            if (plans.GetValueOrDefault($"volume.{id}.monthly.postpaid")?.FirstOrDefault() is { } monthly)
                prices[id] = monthly.Price / OvhServerProvider.CatalogPriceScale;
            else if (plans.GetValueOrDefault($"volume.{id}.consumption")?.FirstOrDefault() is { } hourly)
                prices[id] = hourly.Price / OvhServerProvider.CatalogPriceScale * OvhServerProvider.HoursPerMonth;
        }

        var result = new CachedPrices(DateTimeOffset.UtcNow + PriceCacheLifetime, catalog.Locale.CurrencyCode, prices);
        PriceCache[cacheKey] = result;
        return result;
    }

    private sealed record CachedPrices(DateTimeOffset ExpiresAt, string Currency, Dictionary<string, decimal> PerGb);
}
