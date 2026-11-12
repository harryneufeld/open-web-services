using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Servers;

/// <summary>Reconciles a server's recorded state with what the provider reports.</summary>
public sealed class SyncServer(
    IHostingProviderFactory providers,
    IServerRepository servers,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ProviderServerId is null || server.Status is ServerStatus.Failed or ServerStatus.Deleting)
            return;

        var provider = await providers.GetAsync(server.ProviderId, ct);
        var state = await provider.GetServerAsync(server.ProviderServerId.Value, ct);
        if (state is null)
        {
            server.MarkFailed("The server no longer exists at the provider.");
        }
        else
        {
            Apply(server, state);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private static void Apply(Server server, ProviderServerState state)
    {
        switch (state.Status)
        {
            case ProviderServerStatus.Running: server.MarkRunning(state.Ipv4); break;
            case ProviderServerStatus.Stopped: server.MarkStopped(); break;
        }
    }
}
