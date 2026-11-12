using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Servers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Background;

/// <summary>Periodically refreshes server state (IP, running/stopped) from the provider.</summary>
internal sealed class ServerSyncWorker(IServiceScopeFactory scopes, ILogger<ServerSyncWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly ServerStatus[] Tracked = [ServerStatus.Provisioning, ServerStatus.Running, ServerStatus.Stopped];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await SyncAllAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        try
        {
            var ids = await ListTrackedServerIdsAsync(ct);
            foreach (var id in ids)
                await SyncOneAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Server sync cycle failed.");
        }
    }

    private async Task<List<Guid>> ListTrackedServerIdsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        var ids = new List<Guid>();
        foreach (var status in Tracked)
            ids.AddRange((await repository.ListByStatusAsync(status, ct)).Select(s => s.Id));
        return ids;
    }

    private async Task SyncOneAsync(Guid serverId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SyncServer>().ExecuteAsync(serverId, ct);
        }
        catch (Exception ex) when (ex is ProviderException or ProviderNotConfiguredException or NotFoundException)
        {
            logger.LogWarning("Could not sync server {ServerId}: {Message}", serverId, ex.Message);
        }
    }
}
