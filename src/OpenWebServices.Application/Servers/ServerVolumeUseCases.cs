using System.Text.RegularExpressions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Access;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

/// <param name="Mount">How the server sees the volume; null when ows cannot look.</param>
/// <param name="MonthlyPriceNet">What the volume costs per month, net; null when the provider lists no price.</param>
public sealed record VolumeRow(ProviderVolume Volume, VolumeMount? Mount, decimal? MonthlyPriceNet);

/// <param name="Options">Null when the provider manages no volumes or the server does not exist there yet.</param>
/// <param name="Attachable">Detached volumes in the server's location.</param>
/// <param name="CanMount">ows can sign in to the server, so it formats, mounts, unmounts and grows volumes itself.</param>
/// <param name="MountNote">Why mounts are not shown, e.g. no SSH access; null when they are.</param>
/// <param name="Unavailable">Why <paramref name="Options"/> is null.</param>
public sealed record ServerVolumeView(
    VolumeOptions? Options,
    IReadOnlyList<VolumeRow> Attached,
    IReadOnlyList<ProviderVolume> Attachable,
    bool CanMount,
    string? MountNote,
    string? Unavailable);

/// <summary>A volume as the user wants it: the edit form's values.</summary>
public sealed record VolumeChange(string Name, int SizeGb, string? Type);

/// <summary>
/// Volumes of a server, read and changed live at the provider. When ows can sign in to the server it also formats
/// empty volumes, mounts them with an fstab entry, unmounts them before they are detached and grows the filesystem
/// after a resize, so a volume is ready to use without opening a shell.
/// </summary>
public sealed partial class ServerVolumes(
    AccessContext access,
    IProviderConnectionFactory connections,
    IServerRepository servers,
    IRemoteShell shell,
    ServerAccessWriter writer)
{
    public const int MaxNameLength = 63;

    public async Task<ServerVolumeView> GetAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is not { } externalId)
            return Unavailable("The server has not been created at the provider yet.");

        var connection = await connections.ConnectAsync(server.GroupId, ct);
        if (connection.Volumes is not { } volumes)
            return Unavailable($"ows does not manage volumes at {connection.Type.DisplayName} yet.");

        var options = await volumes.GetOptionsAsync(externalId, ct);
        var all = await volumes.ListVolumesAsync(ct);
        var attached = all.Where(v => v.ServerId == externalId).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var attachable = all
            .Where(v => v.ServerId is null && string.Equals(v.Location, options.Location, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (mounts, note) = await ReadMountsAsync(server, attached, ct);
        var rows = attached.Select(v => new VolumeRow(v, v.DevicePath is { } path ? mounts.GetValueOrDefault(path) : null, Price(options, v)))
            .ToList();
        return new ServerVolumeView(options, rows, attachable, CanMount(server), note, null);
    }

    /// <summary>Creates a volume next to the server, attaches it and, when ows can sign in, formats and mounts it.</summary>
    /// <returns>What happened on the server, for the user.</returns>
    public async Task<string> CreateAsync(Guid serverId, string name, int sizeGb, string? type, string? mountPoint, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var trimmed = NormalizeName(name);
        var (server, volumes) = await ConnectAsync(serverId, ct);
        var options = await volumes.GetOptionsAsync(server.ExternalId!, ct);
        CheckSize(options, sizeGb);
        var chosenType = CheckType(options, type);
        var target = CanMount(server) ? VolumeMounts.NormalizeMountPoint(mountPoint ?? VolumeMounts.DefaultMountPoint(trimmed)) : null;

        var created = await volumes.CreateVolumeAsync(
            new CreateVolumeRequest(trimmed, sizeGb, chosenType, server.ExternalId!, Format: target is null), ct);
        if (target is null)
            return $"{trimmed} is attached. ows has no SSH access to this server, so mount it there yourself.";
        return await MountOrExplainAsync(server, created, target, ct);
    }

    public async Task<string> AttachAsync(Guid serverId, string volumeId, string? mountPoint, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var (server, volumes) = await ConnectAsync(serverId, ct);
        var volume = await FindAsync(volumes, volumeId, ct);
        if (volume.ServerId is not null)
            throw new DomainException($"{volume.Name} is attached to another server. Detach it there first.");
        var target = CanMount(server) ? VolumeMounts.NormalizeMountPoint(mountPoint ?? VolumeMounts.DefaultMountPoint(volume.Name)) : null;

        await volumes.AttachAsync(volumeId, server.ExternalId!, ct);
        if (target is null)
            return $"{volume.Name} is attached. ows has no SSH access to this server, so mount it there yourself.";
        return await MountOrExplainAsync(server, await FindAsync(volumes, volumeId, ct), target, ct);
    }

    /// <summary>Mounts an attached volume, formatting it first when it is empty.</summary>
    public async Task<string> MountAsync(Guid serverId, string volumeId, string mountPoint, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var (server, volumes) = await ConnectAsync(serverId, ct);
        var volume = await FindAttachedAsync(volumes, server, volumeId, ct);
        RequireShell(server);
        return await MountCoreAsync(server, volume, VolumeMounts.NormalizeMountPoint(mountPoint), ct);
    }

    /// <summary>Unmounts the volume when ows can sign in, then detaches it. The data stays on the volume.</summary>
    public async Task DetachAsync(Guid serverId, string volumeId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var (server, volumes) = await ConnectAsync(serverId, ct);
        var volume = await FindAttachedAsync(volumes, server, volumeId, ct);
        if (CanMount(server))
            await UnmountAsync(server, volume, ct);
        await volumes.DetachAsync(volumeId, ct);
    }

    /// <summary>
    /// Applies the edit form: renames, changes the type and grows the volume, in that order, and grows the
    /// filesystem afterwards.
    /// </summary>
    public async Task<string?> SaveAsync(Guid serverId, string volumeId, VolumeChange change, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var name = NormalizeName(change.Name);
        var (server, volumes) = await ConnectAsync(serverId, ct);
        var volume = await FindAttachedAsync(volumes, server, volumeId, ct);
        var options = await volumes.GetOptionsAsync(server.ExternalId!, ct);
        if (volume.IsBusy)
            throw new DomainException($"The provider is still working on {volume.Name}. Reload in a moment.");
        if (change.SizeGb < volume.SizeGb)
            throw new DomainException($"Volumes can only grow. {volume.Name} has {volume.SizeGb} GB.");
        if (change.SizeGb > volume.SizeGb)
            CheckSize(options, change.SizeGb);
        var type = change.Type is null || change.Type == volume.Type ? null : CheckType(options, change.Type);
        if (type is not null && !options.CanChangeType)
            throw new DomainException("This provider has one volume type only.");

        if (name != volume.Name)
            await volumes.RenameAsync(volumeId, name, ct);
        if (type is not null)
            await volumes.ChangeTypeAsync(volumeId, type, ct);
        return change.SizeGb > volume.SizeGb ? await GrowAsync(server, volumes, options, volume with { Name = name }, change.SizeGb, ct) : null;
    }

    /// <summary>Deletes the volume with all its data, unmounting and detaching it first.</summary>
    public async Task DeleteAsync(Guid serverId, string volumeId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var (server, volumes) = await ConnectAsync(serverId, ct);
        var volume = await FindAttachedAsync(volumes, server, volumeId, ct);
        if (CanMount(server))
            await UnmountAsync(server, volume, ct);
        await volumes.DetachAsync(volumeId, ct);
        await volumes.DeleteAsync(volumeId, ct);
    }

    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (!ValidName().IsMatch(trimmed))
            throw new DomainException($"Volume name must be 1-{MaxNameLength} characters: letters, digits, '.', '_' and '-'.");
        return trimmed;
    }

    public static decimal? Price(VolumeOptions options, ProviderVolume volume) => Price(options, volume.Type, volume.SizeGb);

    public static decimal? Price(VolumeOptions options, string? type, int sizeGb) =>
        (options.Types.FirstOrDefault(t => t.Id == type) ?? options.Types.FirstOrDefault())?.PricePerGbMonthNet * sizeGb;

    private async Task<string> GrowAsync(
        Server server, IVolumeProvider volumes, VolumeOptions options, ProviderVolume volume, int sizeGb, CancellationToken ct)
    {
        var shellReady = CanMount(server) && volume.DevicePath is not null;
        if (options.ResizesWhileAttached)
        {
            await volumes.ResizeAsync(volume.ExternalId, sizeGb, ct);
            return shellReady
                ? await RunOrExplainAsync(server, VolumeMounts.GrowScript(volume.DevicePath!, sizeGb), $"{volume.Name} has {sizeGb} GB now", ct)
                : $"{volume.Name} has {sizeGb} GB now. Grow its filesystem on the server, e.g. with resize2fs.";
        }

        // The provider grows detached volumes only: unmount, detach, grow, attach and mount again where it was.
        if (!shellReady)
            throw new DomainException(
                "This provider grows detached volumes only, and ows cannot sign in to the server to unmount it first. " +
                "Unmount and detach it yourself, or give ows SSH access.");
        var mountPoint = (await ReadMountsAsync(server, [volume], ct)).Mounts.GetValueOrDefault(volume.DevicePath!)?.MountPoint;
        await UnmountAsync(server, volume, ct);
        await volumes.DetachAsync(volume.ExternalId, ct);
        await volumes.ResizeAsync(volume.ExternalId, sizeGb, ct);
        await volumes.AttachAsync(volume.ExternalId, server.ExternalId!, ct);
        if (mountPoint is null)
            return $"{volume.Name} has {sizeGb} GB now.";
        var mounted = await FindAsync(volumes, volume.ExternalId, ct);
        await MountCoreAsync(server, mounted, mountPoint, ct);
        return await RunOrExplainAsync(server, VolumeMounts.GrowScript(mounted.DevicePath!, sizeGb), $"{volume.Name} has {sizeGb} GB now", ct);
    }

    private async Task<string> MountOrExplainAsync(Server server, ProviderVolume volume, string mountPoint, CancellationToken ct)
    {
        try
        {
            return await MountCoreAsync(server, volume, mountPoint, ct);
        }
        catch (Exception ex) when (ex is DomainException or RemoteAccessException)
        {
            return $"{volume.Name} is attached, but ows could not mount it: {ex.Message}";
        }
    }

    private async Task<string> MountCoreAsync(Server server, ProviderVolume volume, string mountPoint, CancellationToken ct)
    {
        var device = volume.DevicePath ?? throw new DomainException("The provider does not say where the volume appears on the server.");
        var output = await RunAsync(server, VolumeMounts.MountScript(device, mountPoint), ct);
        return $"{volume.Name}: {VolumeMounts.LastLine(output)}";
    }

    private async Task UnmountAsync(Server server, ProviderVolume volume, CancellationToken ct)
    {
        if (volume.DevicePath is { } device)
            await RunAsync(server, VolumeMounts.UnmountScript(device), ct);
    }

    /// <summary>Runs a follow-up step; a failure there is reported, since the provider change already happened.</summary>
    private async Task<string> RunOrExplainAsync(Server server, string script, string done, CancellationToken ct)
    {
        try
        {
            return $"{done}. {VolumeMounts.LastLine(await RunAsync(server, script, ct))}";
        }
        catch (Exception ex) when (ex is DomainException or RemoteAccessException)
        {
            return $"{done}, but ows could not grow its filesystem: {ex.Message}";
        }
    }

    private async Task<string> RunAsync(Server server, string script, CancellationToken ct)
    {
        var result = await shell.RunAsync(writer.TargetFor(server), script, ct);
        if (ServerAccessWriter.RootLoginRedirect(result) is { } user)
            throw new DomainException(
                $"The server only lets ows in as '{user}' so far. Click Check now under SSH access, then try again.");
        if (!result.Succeeded)
            throw new DomainException(VolumeMounts.LastLine(result.Output) is { Length: > 0 } reason
                ? reason
                : $"The command on the server failed with exit code {result.ExitCode}.");
        return result.Output;
    }

    private async Task<(IReadOnlyDictionary<string, VolumeMount> Mounts, string? Note)> ReadMountsAsync(
        Server server, IReadOnlyList<ProviderVolume> attached, CancellationToken ct)
    {
        var devices = attached.Select(v => v.DevicePath).OfType<string>().ToList();
        if (devices.Count == 0)
            return (new Dictionary<string, VolumeMount>(), null);
        if (!CanMount(server))
            return (new Dictionary<string, VolumeMount>(), server.HasAutomationKey
                ? "Mounts show once the server is running."
                : "ows has no SSH access to this server, so it cannot show or change mounts.");
        try
        {
            var result = await shell.RunAsync(writer.TargetFor(server), VolumeMounts.StatusScript(devices), ct);
            return result.Succeeded
                ? (VolumeMounts.ParseStatus(result.Output), null)
                : (new Dictionary<string, VolumeMount>(), "ows could not read the mounts on the server.");
        }
        catch (RemoteAccessException ex)
        {
            return (new Dictionary<string, VolumeMount>(), $"ows could not read the mounts: {ex.Message}");
        }
    }

    private static bool CanMount(Server server) => server.CanManageKeys;

    private static void RequireShell(Server server)
    {
        if (!CanMount(server))
            throw new DomainException("ows needs SSH access to the running server to mount volumes.");
    }

    private static void CheckSize(VolumeOptions options, int sizeGb)
    {
        if (sizeGb < options.MinSizeGb || sizeGb > options.MaxSizeGb)
            throw new DomainException($"Size must be between {options.MinSizeGb} and {options.MaxSizeGb} GB.");
    }

    private static string? CheckType(VolumeOptions options, string? type)
    {
        if (string.IsNullOrEmpty(type))
            return options.Types.FirstOrDefault()?.Id;
        return options.Types.Any(t => t.Id == type) ? type : throw new DomainException($"'{type}' is not a volume type here.");
    }

    private static async Task<ProviderVolume> FindAsync(IVolumeProvider volumes, string volumeId, CancellationToken ct) =>
        (await volumes.ListVolumesAsync(ct)).FirstOrDefault(v => v.ExternalId == volumeId) ?? throw new NotFoundException("Volume");

    private static async Task<ProviderVolume> FindAttachedAsync(IVolumeProvider volumes, Server server, string volumeId, CancellationToken ct)
    {
        var volume = await FindAsync(volumes, volumeId, ct);
        return volume.ServerId == server.ExternalId ? volume : throw new DomainException($"{volume.Name} is not attached to {server.Name}.");
    }

    private async Task<(Server Server, IVolumeProvider Volumes)> ConnectAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is null)
            throw new DomainException("The server has not been created at the provider yet.");
        return (server, (await connections.ConnectAsync(server.GroupId, ct)).RequireVolumes());
    }

    private static ServerVolumeView Unavailable(string reason) => new(null, [], [], false, null, reason);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,63}$")]
    private static partial Regex ValidName();
}
