using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Apps;
using OpenWebServices.Domain.Apps;

namespace OpenWebServices.Infrastructure.Background;

/// <summary>Picks up pending app installations and runs them one at a time.</summary>
internal sealed class AppInstallWorker(IServiceScopeFactory scopes, ILogger<AppInstallWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunPendingAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunPendingAsync(CancellationToken ct)
    {
        try
        {
            foreach (var id in await ListPendingIdsAsync(ct))
            {
                await using var scope = scopes.CreateSystemScope();
                await scope.ServiceProvider.GetRequiredService<RunAppInstall>().ExecuteAsync(id, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "App install cycle failed.");
        }
    }

    private async Task<List<Guid>> ListPendingIdsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateSystemScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAppInstallRepository>();
        return (await repository.ListByStatusAsync(AppInstallStatus.Pending, ct)).Select(i => i.Id).ToList();
    }
}
