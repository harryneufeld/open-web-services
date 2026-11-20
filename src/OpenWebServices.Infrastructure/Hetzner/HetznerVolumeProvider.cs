using System.Globalization;
using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Hetzner.HetznerServerProvider;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>
/// Hetzner Cloud volumes: one type, 10 GB to 10 TB, attached in the server's location and grown while attached.
/// </summary>
internal sealed class HetznerVolumeProvider(HetznerApiClient api) : IVolumeProvider
{
    public const int MinSizeGb = 10;
    public const int MaxSizeGb = 10240;
    private const string StandardType = "standard";
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(3);

    public async Task<VolumeOptions> GetOptionsAsync(string externalServerId, CancellationToken ct)
    {
        var server = (await api.GetAsync<HetznerServerEnvelope>($"servers/{ParseId(externalServerId)}", ct))?.Server
                     ?? throw new NotFoundException("Server at Hetzner");
        var pricing = (await api.GetAsync<HetznerPricingEnvelope>("pricing", ct))?.Pricing;
        return new VolumeOptions(
            server.Location?.Name ?? "",
            MinSizeGb,
            MaxSizeGb,
            [new VolumeTypeOption(StandardType, "Standard", "SSD block storage, stored three times", pricing?.Volume?.PricePerGbMonth.Net)],
            pricing?.Currency ?? "EUR",
            CanChangeType: false,
            ResizesWhileAttached: true);
    }

    public async Task<IReadOnlyList<ProviderVolume>> ListVolumesAsync(CancellationToken ct) =>
        [.. (await api.GetAllAsync<HetznerVolume>("volumes", "volumes", ct)).Select(ToVolume)];

    public async Task<ProviderVolume> CreateVolumeAsync(CreateVolumeRequest request, CancellationToken ct)
    {
        // With Format, Hetzner formats the volume and mounts it below /mnt/HC_Volume_<id>, for servers ows cannot sign in to.
        var created = await api.PostAsync<HetznerVolumeCreated>("volumes", new HetznerCreateVolumeBody(
            request.Name, request.SizeGb, ParseId(request.ServerId), Automount: request.Format,
            request.Format ? "ext4" : null, new() { ["managed-by"] = "ows" }), ct);
        if (created.Action is { } action)
            await api.WaitAsync(action, ct, CreateTimeout);
        foreach (var next in created.NextActions ?? [])
            await api.WaitAsync(next, ct, CreateTimeout);
        return ToVolume((await api.GetAsync<HetznerVolumeEnvelope>($"volumes/{created.Volume.Id}", ct))?.Volume ?? created.Volume);
    }

    public Task AttachAsync(string volumeId, string externalServerId, CancellationToken ct) =>
        RunActionAsync(volumeId, "attach", new HetznerAttachVolumeBody(ParseId(externalServerId), Automount: false), ct);

    public Task DetachAsync(string volumeId, CancellationToken ct) => RunActionAsync(volumeId, "detach", null, ct);

    public Task RenameAsync(string volumeId, string name, CancellationToken ct) =>
        api.PutAsync($"volumes/{ParseId(volumeId)}", new HetznerRenameBody(name), ct);

    public Task ResizeAsync(string volumeId, int sizeGb, CancellationToken ct) =>
        RunActionAsync(volumeId, "resize", new HetznerResizeVolumeBody(sizeGb), ct);

    public Task ChangeTypeAsync(string volumeId, string type, CancellationToken ct) =>
        throw new ProviderException("Hetzner Cloud has one volume type only.");

    public Task DeleteAsync(string volumeId, CancellationToken ct) => api.DeleteAsync($"volumes/{ParseId(volumeId)}", ct);

    private async Task RunActionAsync(string volumeId, string action, object? body, CancellationToken ct)
    {
        var envelope = await api.PostAsync<HetznerActionEnvelope>($"volumes/{ParseId(volumeId)}/actions/{action}", body, ct);
        await api.WaitAsync(envelope.Action, ct, CreateTimeout);
    }

    private static ProviderVolume ToVolume(HetznerVolume volume) => new(
        volume.Id.ToString(CultureInfo.InvariantCulture),
        volume.Name,
        volume.Size,
        volume.Location?.Name ?? "",
        volume.Server?.ToString(CultureInfo.InvariantCulture),
        StandardType,
        volume.Status ?? "available",
        volume.LinuxDevice,
        IsBusy: volume.Status is "creating");
}
