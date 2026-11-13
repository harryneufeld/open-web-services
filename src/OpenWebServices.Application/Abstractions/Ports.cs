using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Encrypts secrets (API tokens, private keys) before they are persisted.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

public interface ISshKeyGenerator
{
    SshKeyPair Generate(string comment);
}

public interface IProviderGroupRepository
{
    void Add(ProviderGroup group);
    void Remove(ProviderGroup group);
    Task<ProviderGroup?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ProviderGroup>> ListAsync(CancellationToken ct);
}

public interface IServerRepository
{
    void Add(Server server);
    void Remove(Server server);
    Task<Server?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<Server>> ListByGroupAsync(Guid groupId, CancellationToken ct);

    /// <summary>Groups that have servers in Provisioning, Starting or Stopping state.</summary>
    Task<IReadOnlyList<Guid>> ListGroupsWithTransitioningServersAsync(CancellationToken ct);
}

public interface IDnsZoneRepository
{
    void Add(DnsZone zone);
    void Remove(DnsZone zone);
    Task<DnsZone?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<DnsZone>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<DnsZone>> ListByGroupAsync(Guid groupId, CancellationToken ct);
}

public interface IAppInstallRepository
{
    void Add(AppInstall install);
    Task<AppInstall?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AppInstall>> ListForServerAsync(Guid serverId, CancellationToken ct);
    Task<IReadOnlyList<AppInstall>> ListByStatusAsync(AppInstallStatus status, CancellationToken ct);
}

public interface IAppTemplateCatalog
{
    IReadOnlyList<AppTemplate> All { get; }
    AppTemplate? Find(string templateId);
}

/// <summary>Runs commands on a remote server over SSH.</summary>
public interface IRemoteShell
{
    Task WaitUntilReadyAsync(RemoteTarget target, TimeSpan timeout, CancellationToken ct);
    Task<CommandResult> RunAsync(RemoteTarget target, string command, CancellationToken ct);
    Task UploadTextAsync(RemoteTarget target, string remotePath, string content, CancellationToken ct);
}

/// <summary>Asks the background sync to refresh a group soon, e.g. right after a power action.</summary>
public interface ISyncScheduler
{
    void RequestSync(Guid groupId);
}

public static class ResourceTopics
{
    public const string Groups = "groups";
    public const string Servers = "servers";
    public const string DnsZones = "dns-zones";
    public const string AppInstalls = "app-installs";
}

/// <summary>A persisted change to a resource, published after it is saved.</summary>
/// <param name="Topic">One of <see cref="ResourceTopics"/>.</param>
public sealed record ResourceChange(string Topic, Guid Id);

/// <summary>In-process notifications so open pages refresh as soon as data changes.</summary>
public interface IChangeNotifier
{
    event Action<ResourceChange>? Changed;

    void Publish(ResourceChange change);
}
