using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Application.Servers;

public sealed class DeleteServer(
    IProviderConnectionFactory connections,
    IServerRepository servers,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");

        server.MarkDeleting();
        await unitOfWork.SaveChangesAsync(ct);

        // Failures leave the record in Deleting state so the user can retry; nothing is orphaned silently.
        if (server.ExternalId is not null || server.ExternalSshKeyId is not null)
        {
            var provider = (await connections.ConnectAsync(server.GroupId, ct)).RequireServers();
            if (server.ExternalId is { } externalId)
                await provider.DeleteServerAsync(externalId, ct);
            if (server.ExternalSshKeyId is { } keyId)
                await provider.DeleteSshKeyAsync(keyId, ct);
        }

        servers.Remove(server);
        await unitOfWork.SaveChangesAsync(ct);
    }
}
