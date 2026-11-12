using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;

namespace OpenWebServices.Application.Servers;

public sealed class ControlServerPower(IHostingProviderFactory providers, IServerRepository servers)
{
    public async Task ExecuteAsync(Guid serverId, PowerAction action, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ProviderServerId is null)
            throw new DomainException("The server has not been created at the provider yet.");

        var provider = await providers.GetAsync(server.ProviderId, ct);
        await provider.PowerAsync(server.ProviderServerId.Value, action, ct);
    }
}
