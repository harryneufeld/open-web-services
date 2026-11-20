namespace OpenWebServices.Application.Abstractions;

/// <summary>A block storage volume at a provider.</summary>
/// <param name="ServerId">External id of the server it is attached to; null when detached.</param>
/// <param name="Type">The provider's volume type id, e.g. "high-speed"; null for providers with one type.</param>
/// <param name="DevicePath">
/// The stable device path on the attached server, e.g. /dev/disk/by-id/scsi-0HC_Volume_1. Null when unknown.
/// </param>
/// <param name="IsBusy">The provider is still working on the volume (creating, attaching, resizing, ...).</param>
public sealed record ProviderVolume(
    string ExternalId,
    string Name,
    int SizeGb,
    string Location,
    string? ServerId,
    string? Type,
    string Status,
    string? DevicePath,
    bool IsBusy = false);

/// <param name="Id">Provider id passed back on create and change, e.g. "high-speed-gen2".</param>
/// <param name="PricePerGbMonthNet">Net price of one GB for one month; null when the provider does not list it.</param>
public sealed record VolumeTypeOption(string Id, string Label, string? Description, decimal? PricePerGbMonthNet);

/// <summary>What volumes next to one server can be, and which changes the provider allows.</summary>
/// <param name="Location">The server's location; volumes there can attach to it.</param>
/// <param name="Types">Selectable types; one entry for providers without types.</param>
/// <param name="CanChangeType">An existing volume can move to another type.</param>
/// <param name="ResizesWhileAttached">
/// The provider grows attached volumes in place. When false, ows detaches the volume, grows it and attaches it again.
/// </param>
public sealed record VolumeOptions(
    string Location,
    int MinSizeGb,
    int MaxSizeGb,
    IReadOnlyList<VolumeTypeOption> Types,
    string Currency,
    bool CanChangeType,
    bool ResizesWhileAttached);

/// <param name="Type">One of <see cref="VolumeOptions.Types"/>; null for the default.</param>
/// <param name="Format">
/// Let the provider format the new volume, where it can, so it is usable without ows mounting it. ows formats empty
/// volumes itself when it mounts them.
/// </param>
public sealed record CreateVolumeRequest(string Name, int SizeGb, string? Type, string ServerId, bool Format);

/// <summary>
/// Block storage volumes. Volumes live in one location and attach to servers there; ows creates them next to a server
/// and attaches them right away.
/// </summary>
public interface IVolumeProvider
{
    Task<VolumeOptions> GetOptionsAsync(string externalServerId, CancellationToken ct);

    /// <summary>All volumes of the project, attached or not.</summary>
    Task<IReadOnlyList<ProviderVolume>> ListVolumesAsync(CancellationToken ct);

    /// <summary>Creates a volume in the server's location and attaches it to the server.</summary>
    Task<ProviderVolume> CreateVolumeAsync(CreateVolumeRequest request, CancellationToken ct);

    Task AttachAsync(string volumeId, string externalServerId, CancellationToken ct);

    Task DetachAsync(string volumeId, CancellationToken ct);

    Task RenameAsync(string volumeId, string name, CancellationToken ct);

    /// <summary>Grows the volume. Volumes never shrink.</summary>
    Task ResizeAsync(string volumeId, int sizeGb, CancellationToken ct);

    /// <exception cref="ProviderException">The provider has a single type (<see cref="VolumeOptions.CanChangeType"/>).</exception>
    Task ChangeTypeAsync(string volumeId, string type, CancellationToken ct);

    /// <summary>Deletes a detached volume and its data.</summary>
    Task DeleteAsync(string volumeId, CancellationToken ct);
}
