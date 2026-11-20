using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

/// <param name="Options">Null when the provider cannot rescale or the server does not exist there yet.</param>
/// <param name="Unavailable">Why <paramref name="Options"/> is null.</param>
public sealed record RescaleView(RescaleOptions? Options, string? Unavailable);

/// <summary>
/// Moves a server to another server type. The server shows as rescaling until the provider reports the new type;
/// the background sync then picks up the new state and price.
/// </summary>
public sealed class RescaleServer(
    AccessContext access,
    IProviderConnectionFactory connections,
    IServerRepository servers,
    ISyncScheduler syncScheduler,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<RescaleView> GetAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is not { } externalId)
            return new RescaleView(null, "The server has not been created at the provider yet.");
        var connection = await connections.ConnectAsync(server.GroupId, ct);
        if (connection.Rescale is not { } rescale)
            return new RescaleView(null, $"ows cannot rescale servers at {connection.Type.DisplayName} yet.");
        return new RescaleView(await rescale.GetRescaleOptionsAsync(externalId, ct), null);
    }

    /// <param name="growDisk">Grow the disk as well, where the provider lets the disk keep its size.</param>
    public async Task ExecuteAsync(Guid serverId, string typeName, bool growDisk, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (!server.CanRescale)
            throw new DomainException(server.ExternalId is null
                ? "The server has not been created at the provider yet."
                : "A server can be rescaled once it is running or stopped.");

        var connection = await connections.ConnectAsync(server.GroupId, ct);
        var rescale = connection.RequireRescale();
        var options = await rescale.GetRescaleOptionsAsync(server.ExternalId!, ct);
        var target = options.Targets.FirstOrDefault(t => string.Equals(t.TypeName, typeName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new DomainException($"{server.Name} cannot move to {typeName}.");
        if (!target.Available)
            throw new DomainException($"{target.TypeName} is sold out in {target.LocationCity} right now. Pick another type or try later.");

        server.BeginRescale(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(ct); // shows "Rescaling" right away; providers that stop the server take minutes
        try
        {
            await rescale.RescaleAsync(server.ExternalId!, target.TypeName, growDisk && options.DiskIsOptional, ct);
        }
        catch
        {
            server.EndRescale();
            await RefreshAsync(connection, server, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            syncScheduler.RequestSync(server.GroupId);
            throw;
        }

        await RefreshAsync(connection, server, ct);
        await unitOfWork.SaveChangesAsync(ct);
        syncScheduler.RequestSync(server.GroupId);
    }

    /// <summary>Applies what the provider reports now; a provider still resizing keeps the server rescaling.</summary>
    private async Task RefreshAsync(IProviderConnection connection, Server server, CancellationToken ct)
    {
        try
        {
            if (await connection.RequireServers().GetServerAsync(server.ExternalId!, ct) is not { } observed)
                return;
            server.ApplyObservedType(observed.TypeName, observed.MonthlyNet, observed.Currency);
            server.ApplyObserved(observed.State, observed.Ipv4, clock.UtcNow);
        }
        catch (ProviderException)
        {
            // the background sync catches up
        }
    }
}
