namespace OpenWebServices.Application.Abstractions;

/// <summary>The server types one server can move to, priced at its location and billing.</summary>
/// <param name="Current">The server's type as an offer; null when the provider no longer sells it.</param>
/// <param name="DiskGb">The size of the server's disk now. Disks never shrink, so targets have at least this much.</param>
/// <param name="Targets">Types the provider can move the server to, cheapest first. Sold-out types are not available.</param>
/// <param name="StopsServer">
/// The provider changes the type of stopped servers only: ows shuts a running server down first and starts it again
/// afterwards.
/// </param>
/// <param name="DiskIsOptional">
/// The disk can stay at its size, so the server can move back to a smaller type later. Otherwise the disk always grows
/// to the new type's size.
/// </param>
/// <param name="Note">What the provider does during a rescale, for the user.</param>
public sealed record RescaleOptions(
    ServerOffer? Current,
    int DiskGb,
    IReadOnlyList<ServerOffer> Targets,
    bool StopsServer,
    bool DiskIsOptional,
    string Note);

/// <summary>Moves servers to another server type (more or fewer vCPUs and memory).</summary>
public interface IServerRescaleProvider
{
    Task<RescaleOptions> GetRescaleOptionsAsync(string externalServerId, CancellationToken ct);

    /// <summary>
    /// Moves the server to <paramref name="typeName"/>. Providers that need a stopped server shut it down, wait for the
    /// change and start it again when it was running; others return once the provider accepted the change.
    /// </summary>
    /// <param name="growDisk">
    /// Grow the disk to the new type's size. Only honoured when <see cref="RescaleOptions.DiskIsOptional"/>; the disk
    /// always grows otherwise.
    /// </param>
    Task RescaleAsync(string externalServerId, string typeName, bool growDisk, CancellationToken ct);
}
