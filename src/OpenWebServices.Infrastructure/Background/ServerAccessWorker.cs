using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Access;

namespace OpenWebServices.Infrastructure.Background;

/// <summary>
/// Brings servers' authorized_keys in line with their grants: new servers once they run, changes made while a
/// server was off, retired ows keys, and failed attempts after a pause.
/// </summary>
internal sealed class ServerAccessWorker(IServiceScopeFactory scopes, IClock clock, ILogger<ServerAccessWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunAsync(stoppingToken);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            foreach (var serverId in await ListDueAsync(ct))
            {
                await using var scope = scopes.CreateSystemScope();
                await scope.ServiceProvider.GetRequiredService<ServerAccess>().ApplyInBackgroundAsync(serverId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Server access cycle failed.");
        }
    }

    private async Task<List<Guid>> ListDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateSystemScope();
        var servers = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        var grants = scope.ServiceProvider.GetRequiredService<IServerKeyGrantRepository>();
        var due = new HashSet<Guid>(await servers.ListServersNeedingAccessCheckAsync(clock.UtcNow - RetryAfter, ct));
        due.UnionWith(await grants.ListServersWithPendingChangesAsync(ct));
        return [.. due];
    }
}
