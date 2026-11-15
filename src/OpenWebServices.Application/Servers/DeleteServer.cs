using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

public sealed class DeleteServer(
    AccessContext access,
    IProviderConnectionFactory connections,
    IServerRepository servers,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid serverId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");

        server.MarkDeleting();
        await unitOfWork.SaveChangesAsync(ct);

        // Failures leave the record in Deleting state so the user can retry; nothing is orphaned silently.
        if (server.ExternalId is not null || server.ExternalSshKeyId is not null)
        {
            var connection = await connections.ConnectAsync(server.GroupId, ct);
            var provider = connection.RequireServers();
            if (server.ExternalId is { } externalId)
            {
                await provider.DeleteServerAsync(externalId, ct);
                await RemoveFirewallAsync(connection, externalId, ct);
            }
            if (server.ExternalSshKeyId is { } keyId)
                await provider.DeleteSshKeyAsync(keyId, ct);
        }

        servers.Remove(server);
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The server's ows firewall would stay behind, unused, at the provider. Cleaning it up is best effort: the server
    /// is already gone, so a failure here must not leave the record stuck in Deleting.
    /// </summary>
    private static async Task RemoveFirewallAsync(IProviderConnection connection, string externalId, CancellationToken ct)
    {
        if (connection.Firewalls is not { } firewalls)
            return;
        try
        {
            await firewalls.RemoveServerFirewallAsync(externalId, ct);
        }
        catch (Exception ex) when (ex is ProviderException or NotFoundException)
        {
        }
    }
}
