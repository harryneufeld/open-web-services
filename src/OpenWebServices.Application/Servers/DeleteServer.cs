using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Application.Servers;

public sealed class DeleteServer(
    IHostingProviderFactory providers,
    IServerRepository servers,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");

        server.MarkDeleting();
        await unitOfWork.SaveChangesAsync(ct);

        // Failures leave the record in Deleting state so the user can retry; nothing is orphaned silently.
        if (server.ProviderServerId is not null || server.ProviderSshKeyId is not null)
        {
            var provider = await providers.GetAsync(server.ProviderId, ct);
            if (server.ProviderServerId is { } serverId1)
                await provider.DeleteServerAsync(serverId1, ct);
            if (server.ProviderSshKeyId is { } keyId)
                await provider.DeleteSshKeyAsync(keyId, ct);
        }

        servers.Remove(server);
        await unitOfWork.SaveChangesAsync(ct);
    }
}
