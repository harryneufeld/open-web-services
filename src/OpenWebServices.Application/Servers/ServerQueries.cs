using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Servers;

public sealed record ServerView(Server Server, string GroupName, string ProviderName);

/// <summary>Read-side use cases for the UI.</summary>
public sealed class ServerQueries(
    IServerRepository servers,
    IProviderGroupRepository groups,
    IProviderCatalog catalog,
    IAppInstallRepository installs)
{
    public async Task<IReadOnlyList<ServerView>> ListAsync(CancellationToken ct)
    {
        var groupsById = (await groups.ListAsync(ct)).ToDictionary(g => g.Id);
        return [.. (await servers.ListAsync(ct)).Select(s => ToView(s, groupsById.GetValueOrDefault(s.GroupId)))];
    }

    public async Task<ServerView> GetAsync(Guid id, CancellationToken ct)
    {
        var server = await servers.FindAsync(id, ct) ?? throw new NotFoundException("Server");
        return ToView(server, await groups.FindAsync(server.GroupId, ct));
    }

    public Task<IReadOnlyList<AppInstall>> ListInstallsAsync(Guid serverId, CancellationToken ct) =>
        installs.ListForServerAsync(serverId, ct);

    private ServerView ToView(Server server, Domain.Providers.ProviderGroup? group) => new(
        server,
        group?.Name ?? "(unknown group)",
        group is null ? "" : catalog.Find(group.ProviderType)?.DisplayName ?? group.ProviderType);
}
