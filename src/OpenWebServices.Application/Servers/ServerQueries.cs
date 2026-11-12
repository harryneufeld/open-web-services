using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Servers;

/// <summary>Read-side use cases for the UI.</summary>
public sealed class ServerQueries(IServerRepository servers, IAppInstallRepository installs)
{
    public Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct) => servers.ListAsync(ct);

    public async Task<Server> GetAsync(Guid id, CancellationToken ct) =>
        await servers.FindAsync(id, ct) ?? throw new NotFoundException("Server");

    public Task<IReadOnlyList<AppInstall>> ListInstallsAsync(Guid serverId, CancellationToken ct) =>
        installs.ListForServerAsync(serverId, ct);
}
