using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Hetzner.HetznerServerProvider;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>
/// Hetzner Cloud changes the type of stopped servers only, to types of the same CPU architecture whose disk is at
/// least as large. The disk can keep its size, so the server can move back to a smaller type later.
/// </summary>
internal sealed class HetznerRescaleProvider(HetznerApiClient api) : IServerRescaleProvider
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(3);

    // Moving to another host copies the disk, which can take a while for large disks.
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(15);

    public async Task<RescaleOptions> GetRescaleOptionsAsync(string externalServerId, CancellationToken ct)
    {
        var server = await GetAsync(externalServerId, ct);
        var location = server.Location?.Name ?? throw new ProviderException("Hetzner does not say where the server is.");
        var current = server.ServerType ?? throw new ProviderException("Hetzner does not say which type the server has.");
        var diskGb = server.PrimaryDiskSize ?? current.Disk;

        var types = await api.GetAllAsync<HetznerServerType>("server_types", "server_types", ct);
        var locations = (await api.GetAllAsync<HetznerLocation>("locations", "locations", ct)).ToDictionary(l => l.Name);
        var available = await LoadMigrationTargetsAsync(location, ct);
        ServerOffer? Offer(HetznerServerType type) =>
            type.Prices.FirstOrDefault(p => p.Location == location) is { } price
                ? ToOffer(type, price, locations, available is null || available.Contains(type.Id))
                : null;

        var targets = types
            .Where(t => t.Id != current.Id && t.Deprecation is null && !IsDeprecatedAt(t, location)
                        && t.Architecture == current.Architecture && t.Disk >= diskGb)
            .Select(Offer)
            .OfType<ServerOffer>()
            .OrderBy(o => o.MonthlyNet)
            .ThenBy(o => o.TypeName, StringComparer.Ordinal)
            .ToList();

        return new RescaleOptions(
            Offer(types.FirstOrDefault(t => t.Id == current.Id) ?? current) is { } now ? now with { Available = true } : null,
            diskGb,
            targets,
            StopsServer: true,
            DiskIsOptional: true,
            "Hetzner changes the type of stopped servers only. ows shuts the server down, changes the type and starts it " +
            "again, which usually takes a few minutes. The IP addresses stay the same.");
    }

    public async Task RescaleAsync(string externalServerId, string typeName, bool growDisk, CancellationToken ct)
    {
        var id = ParseId(externalServerId);
        var server = await GetAsync(externalServerId, ct);
        var wasRunning = server.Status != "off";
        if (wasRunning)
            await ShutDownAsync(id, server.Name, ct);

        var change = await api.PostAsync<HetznerActionEnvelope>(
            $"servers/{id}/actions/change_type", new HetznerChangeTypeBody(typeName, growDisk), ct);
        try
        {
            await api.WaitAsync(change.Action, ct, ChangeTimeout);
        }
        catch (ProviderException) when (wasRunning)
        {
            // Hetzner keeps the old type when the change fails; the server should not stay off because of ows.
            await api.PostAsync($"servers/{id}/actions/poweron", CancellationToken.None);
            throw;
        }

        if (wasRunning)
            await api.PostAsync($"servers/{id}/actions/poweron", ct);
    }

    /// <summary>Sends an ACPI shutdown and waits until the server is off. Hetzner's action only confirms the signal.</summary>
    private async Task ShutDownAsync(long id, string name, CancellationToken ct)
    {
        await api.PostAsync($"servers/{id}/actions/shutdown", ct);
        var deadline = DateTime.UtcNow + ShutdownTimeout;
        while ((await GetAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture), ct)).Status != "off")
        {
            if (DateTime.UtcNow > deadline)
                throw new ProviderException(
                    $"{name} did not shut down within {ShutdownTimeout.TotalMinutes:0} minutes. Shut it down on the server " +
                    "or power it off, then rescale it while it is stopped.");
            await Task.Delay(HetznerApiClient.ActionPollInterval * 3, ct);
        }
    }

    /// <summary>Ids of the types servers in the location can change to; null when Hetzner does not say.</summary>
    private async Task<HashSet<long>?> LoadMigrationTargetsAsync(string location, CancellationToken ct)
    {
        try
        {
            var datacenters = (await api.GetAllAsync<HetznerDatacenter>("datacenters", "datacenters", ct))
                .Where(d => d.Location.Name == location)
                .ToList();
            return datacenters.Count == 0
                ? null
                : datacenters.SelectMany(d => d.ServerTypes.AvailableForMigration ?? d.ServerTypes.Available).ToHashSet();
        }
        catch (ProviderException)
        {
            return null; // unknown: Hetzner rejects a sold-out type when the change is requested
        }
    }

    private async Task<HetznerServer> GetAsync(string externalServerId, CancellationToken ct) =>
        (await api.GetAsync<HetznerServerEnvelope>($"servers/{ParseId(externalServerId)}", ct))?.Server
        ?? throw new NotFoundException("Server at Hetzner");
}
