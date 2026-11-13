using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Background;

/// <summary>In-process pub/sub. One app instance serves the UI, so no message broker is needed.</summary>
internal sealed class InProcessChangeNotifier(ILogger<InProcessChangeNotifier> logger) : IChangeNotifier
{
    public event Action<ResourceChange>? Changed;

    public void Publish(ResourceChange change)
    {
        if (Changed is not { } handlers)
            return;

        // A failing subscriber (e.g. a closed browser tab) must not break the save that published.
        foreach (var handler in handlers.GetInvocationList().Cast<Action<ResourceChange>>())
        {
            try { handler(change); }
            catch (Exception ex) { logger.LogWarning(ex, "A change subscriber failed."); }
        }
    }
}

/// <summary>Collects groups that should be synced on the next worker tick.</summary>
internal sealed class SyncScheduler : ISyncScheduler
{
    private readonly ConcurrentDictionary<Guid, byte> _requested = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void RequestSync(Guid groupId)
    {
        _requested[groupId] = 0;
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    /// <summary>Returns as soon as a sync is requested or the timeout elapses.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    public IReadOnlyCollection<Guid> TakeRequested()
    {
        var ids = _requested.Keys.ToList();
        foreach (var id in ids)
            _requested.TryRemove(id, out _);
        return ids;
    }
}
