using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

/// <summary>
/// Sends a power action and shows it as in progress right away. The background sync then polls the group
/// quickly until the provider reports the new state.
/// </summary>
public sealed class ControlServerPower(
    AccessContext access,
    IProviderConnectionFactory connections,
    IServerRepository servers,
    ISyncScheduler syncScheduler,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(Guid serverId, PowerAction action, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is null)
            throw new DomainException("The server has not been created at the provider yet.");

        var provider = (await connections.ConnectAsync(server.GroupId, ct)).RequireServers();
        await provider.PowerAsync(server.ExternalId, action, ct);

        if (action == PowerAction.Shutdown)
            server.MarkStopping(clock.UtcNow);
        else
            server.MarkStarting(clock.UtcNow);

        await unitOfWork.SaveChangesAsync(ct);
        syncScheduler.RequestSync(server.GroupId);
    }
}
