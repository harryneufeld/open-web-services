using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Sync;

namespace OpenWebServices.Infrastructure.Background;

/// <summary>
/// Keeps every group in sync with its provider: a full sync every <see cref="IdleInterval"/>, every
/// <see cref="BusyInterval"/> while a server is changing state, and immediately when a use case asks for it.
/// </summary>
internal sealed class GroupSyncWorker(
    IServiceScopeFactory scopes,
    SyncScheduler scheduler,
    IClock clock,
    ILogger<GroupSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

    private readonly Dictionary<Guid, DateTime> _lastSync = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncDueGroupsAsync(stoppingToken);
                await scheduler.WaitAsync(Tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Group sync cycle failed.");
                await Task.Delay(Tick, stoppingToken);
            }
        }
    }

    private async Task SyncDueGroupsAsync(CancellationToken ct)
    {
        var requested = scheduler.TakeRequested();
        List<Guid> groupIds;
        IReadOnlyList<Guid> busy;
        await using (var scope = scopes.CreateSystemScope())
        {
            groupIds = [.. (await scope.ServiceProvider.GetRequiredService<IProviderGroupRepository>().ListAsync(ct)).Select(g => g.Id)];
            busy = await scope.ServiceProvider.GetRequiredService<IServerRepository>().ListGroupsWithTransitioningServersAsync(ct);
        }

        foreach (var key in _lastSync.Keys.Except(groupIds).ToList())
            _lastSync.Remove(key);

        var now = clock.UtcNow;
        foreach (var groupId in groupIds)
        {
            var since = now - _lastSync.GetValueOrDefault(groupId, DateTime.MinValue);
            var due = requested.Contains(groupId)
                      || since >= IdleInterval
                      || (busy.Contains(groupId) && since >= BusyInterval);
            if (due)
                await SyncAsync(groupId, ct);
        }
    }

    private async Task SyncAsync(Guid groupId, CancellationToken ct)
    {
        _lastSync[groupId] = clock.UtcNow;
        try
        {
            await using var scope = scopes.CreateSystemScope();
            var report = await scope.ServiceProvider.GetRequiredService<SyncGroup>().ExecuteAsync(groupId, ct);
            if (!report.Succeeded)
                logger.LogWarning("Sync of group {GroupId} failed: {Error}", groupId, report.Error);
        }
        catch (NotFoundException)
        {
            // group removed meanwhile
        }
    }
}
