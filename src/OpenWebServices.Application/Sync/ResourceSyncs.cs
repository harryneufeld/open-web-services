using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Sync;

public sealed class ServerInventorySync(IServerRepository servers, IClock clock) : IResourceSync
{
    public const string MissingReason = "The server no longer exists at the provider.";

    public ResourceKind Kind => ResourceKind.Servers;

    public async Task<SyncResult> SyncAsync(ProviderGroup group, IProviderConnection connection, CancellationToken ct)
    {
        var provider = connection.RequireServers();
        var remote = await provider.ListServersAsync(ct);
        var local = await servers.ListByGroupAsync(group.Id, ct);
        var known = local.Where(s => s.ExternalId is not null).ToDictionary(s => s.ExternalId!);
        int added = 0, updated = 0, removed = 0;

        foreach (var listed in remote)
        {
            var observed = listed;
            if (known.TryGetValue(observed.ExternalId, out var server))
            {
                // Lists can lag behind a resize or leave the type out (OVHcloud lists only a flavor id), so a server
                // that is being rescaled is looked up directly.
                if (server.Status == ServerStatus.Rescaling)
                    observed = await provider.GetServerAsync(observed.ExternalId, ct) ?? observed;
                server.ApplyObservedType(observed.TypeName, observed.MonthlyNet, observed.Currency);
                server.ApplyObserved(observed.State, observed.Ipv4, clock.UtcNow);
                updated++;
            }
            else if (!IsBeingProvisioned(local, observed))
            {
                servers.Add(Server.Import(
                    group.TenantId, group.Id, observed.ExternalId, observed.Name, observed.State, observed.Ipv4, observed.Location,
                    observed.TypeName, observed.Image, observed.MonthlyNet, observed.Currency, clock.UtcNow));
                added++;
            }
        }

        var remoteIds = remote.Select(r => r.ExternalId).ToHashSet();
        foreach (var server in known.Values.Where(s => !remoteIds.Contains(s.ExternalId!) && s.Status != ServerStatus.Deleting))
        {
            // Provider lists can lag behind (OVHcloud lists a new instance only after a few seconds), so a server
            // missing from the list is looked up directly; only a definite "not found" counts as gone.
            if (await provider.GetServerAsync(server.ExternalId!, ct) is { } direct)
            {
                server.ApplyObservedType(direct.TypeName, direct.MonthlyNet, direct.Currency);
                server.ApplyObserved(direct.State, direct.Ipv4, clock.UtcNow);
                updated++;
            }
            else if (server.Origin == ServerOrigin.Imported)
            {
                servers.Remove(server); // deleted outside the platform: mirror that
                removed++;
            }
            else if (server.Status != ServerStatus.Failed)
            {
                server.MarkFailed(MissingReason); // keep it: it holds the SSH key and install history
                removed++;
            }
        }

        return new SyncResult(Kind, added, updated, removed);
    }

    /// <summary>
    /// A server we are creating right now exists at the provider before its id is saved here.
    /// Importing it would create a duplicate.
    /// </summary>
    private static bool IsBeingProvisioned(IEnumerable<Server> local, ProviderServer observed) =>
        local.Any(s => s.ExternalId is null && s.Status == ServerStatus.Provisioning && s.Name == observed.Name);
}

public sealed class DnsZoneInventorySync(IDnsZoneRepository zones, IClock clock) : IResourceSync
{
    public ResourceKind Kind => ResourceKind.Dns;

    public async Task<SyncResult> SyncAsync(ProviderGroup group, IProviderConnection connection, CancellationToken ct)
    {
        var remote = await connection.RequireDns().ListZonesAsync(ct);
        var local = (await zones.ListByGroupAsync(group.Id, ct)).ToDictionary(z => z.ExternalId);
        int added = 0, updated = 0, removed = 0;

        foreach (var observed in remote)
        {
            if (!local.TryGetValue(observed.ExternalId, out var zone))
            {
                zone = DnsZone.Track(group.TenantId, group.Id, observed.ExternalId, observed.Name, clock.UtcNow);
                zones.Add(zone);
                added++;
            }
            else
            {
                updated++;
            }

            zone.Update(observed.Status, observed.Ttl, observed.RecordCount, observed.Nameservers,
                observed.DelegationStatus, clock.UtcNow);
        }

        var remoteIds = remote.Select(z => z.ExternalId).ToHashSet();
        foreach (var zone in local.Values.Where(z => !remoteIds.Contains(z.ExternalId)))
        {
            zones.Remove(zone);
            removed++;
        }

        return new SyncResult(Kind, added, updated, removed);
    }
}
