using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.SshKeys;

namespace OpenWebServices.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Encrypts secrets (API tokens, private keys) before they are persisted. Every secret is bound to its tenant and
/// purpose: a ciphertext copied to another tenant's row or another field cannot be decrypted there.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText, Guid tenantId, string purpose);

    /// <exception cref="System.Security.Cryptography.CryptographicException">Wrong tenant, purpose or key.</exception>
    string Unprotect(string protectedText, Guid tenantId, string purpose);
}

public static class SecretPurposes
{
    public const string ProviderCredentials = "provider-credentials";
    public const string SshPrivateKey = "ssh-private-key";
}

/// <summary>Creates the key pairs ows itself signs in with. Users' key pairs are never generated on the server.</summary>
public interface ISshKeyGenerator
{
    SshKeyPair Generate(string comment);

    /// <summary>The authorized_keys line of an existing private key.</summary>
    string PublicKeyOf(string privateKeyPem, string comment);
}

public interface ISshKeyRepository
{
    void Add(SshKey key);
    Task<SshKey?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>All keys of the current tenant, revoked ones included.</summary>
    Task<IReadOnlyList<SshKey>> ListAsync(CancellationToken ct);

    Task<IReadOnlyList<SshKey>> ListByOwnerAsync(Guid tenantId, Guid ownerUserId, CancellationToken ct);
}

public interface IServerKeyGrantRepository
{
    void Add(ServerKeyGrant grant);
    void Remove(ServerKeyGrant grant);
    Task<IReadOnlyList<ServerKeyGrant>> ListForServerAsync(Guid serverId, CancellationToken ct);
    Task<IReadOnlyList<ServerKeyGrant>> ListForKeyAsync(Guid sshKeyId, CancellationToken ct);

    /// <summary>Servers with a grant change that has not reached the server yet.</summary>
    Task<IReadOnlyList<Guid>> ListServersWithPendingChangesAsync(CancellationToken ct);
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

    /// <summary>
    /// Running servers ows holds a key for whose authorized_keys need attention: never checked, a retired automation
    /// key to remove, or a last check older than <paramref name="checkedBeforeUtc"/> that failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListServersNeedingAccessCheckAsync(DateTime checkedBeforeUtc, CancellationToken ct);
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
    /// <summary>Waits until ows can sign in, and returns the host key the server presented.</summary>
    /// <exception cref="HostKeyMismatchException">The target has a pinned host key and the server presented another.</exception>
    Task<HostKey> WaitUntilReadyAsync(RemoteTarget target, TimeSpan timeout, CancellationToken ct);

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
    public const string SshKeys = "ssh-keys";
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
