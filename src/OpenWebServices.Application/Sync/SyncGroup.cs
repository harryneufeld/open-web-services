using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Application.Sync;

public sealed record SyncResult(ResourceKind Kind, int Added, int Updated, int Removed)
{
    public override string ToString() =>
        $"{ResourceKinds.Label(Kind)}: {Added} imported, {Updated} updated, {Removed} removed";
}

public sealed record SyncReport(IReadOnlyList<SyncResult> Results, string? Error)
{
    public bool Succeeded => Error is null;

    public override string ToString() =>
        Results.Count == 0 && Error is null ? "Nothing to sync." : string.Join("; ", Results) + (Error is null ? "" : $" — {Error}");
}

/// <summary>
/// Reconciles one kind of resource of a group with the provider: imports what is new, refreshes what is known,
/// and handles what disappeared. Add an implementation per <see cref="ResourceKind"/>.
/// </summary>
public interface IResourceSync
{
    ResourceKind Kind { get; }

    Task<SyncResult> SyncAsync(ProviderGroup group, IProviderConnection connection, CancellationToken ct);
}

/// <summary>Syncs every enabled resource kind of a group. Never throws for provider errors; they end up on the group.</summary>
public sealed class SyncGroup(
    IProviderGroupRepository groups,
    IProviderConnectionFactory connections,
    IEnumerable<IResourceSync> syncs,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<SyncReport> ExecuteAsync(Guid groupId, CancellationToken ct)
    {
        var group = await groups.FindAsync(groupId, ct) ?? throw new NotFoundException("Provider group");
        var results = new List<SyncResult>();
        string? error = null;

        try
        {
            var connection = await connections.ConnectAsync(groupId, ct);
            foreach (var sync in syncs.Where(s => group.IsEnabled(s.Kind) && connection.Type.SupportedResources.HasFlag(s.Kind)))
                results.Add(await sync.SyncAsync(group, connection, ct));
            group.RecordSyncSuccess(clock.UtcNow);
        }
        catch (Exception ex) when (ex is ProviderException or ProviderNotConfiguredException or HttpRequestException)
        {
            error = ex.Message;
            group.RecordSyncFailure(ex.Message, clock.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return new SyncReport(results, error);
    }
}
