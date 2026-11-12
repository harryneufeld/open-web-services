using OpenWebServices.Domain.Apps;
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

public interface IServerRepository
{
    void Add(Server server);
    void Remove(Server server);
    Task<Server?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<Server>> ListByStatusAsync(ServerStatus status, CancellationToken ct);
}

public interface IAppInstallRepository
{
    void Add(AppInstall install);
    Task<AppInstall?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AppInstall>> ListForServerAsync(Guid serverId, CancellationToken ct);
    Task<IReadOnlyList<AppInstall>> ListByStatusAsync(AppInstallStatus status, CancellationToken ct);
}

public interface IProviderAccountRepository
{
    void Add(ProviderAccount account);
    Task<ProviderAccount?> FindByProviderAsync(string providerId, CancellationToken ct);
}

public interface IAppTemplateCatalog
{
    IReadOnlyList<AppTemplate> All { get; }
    AppTemplate? Find(string templateId);
}

/// <summary>One hosting provider (Hetzner, Scaleway, ...). Implemented in Infrastructure.</summary>
public interface IHostingProvider
{
    string Id { get; }
    Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct);
    Task<long> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct);
    Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct);
    Task<ProviderServerState?> GetServerAsync(long providerServerId, CancellationToken ct);
    Task PowerAsync(long providerServerId, PowerAction action, CancellationToken ct);
    Task DeleteServerAsync(long providerServerId, CancellationToken ct);
    Task DeleteSshKeyAsync(long providerSshKeyId, CancellationToken ct);
}

public interface IHostingProviderFactory
{
    IReadOnlyCollection<string> ProviderIds { get; }

    /// <exception cref="ProviderNotConfiguredException">No credentials stored for the provider.</exception>
    Task<IHostingProvider> GetAsync(string providerId, CancellationToken ct);
}

/// <summary>Runs commands on a remote server over SSH.</summary>
public interface IRemoteShell
{
    Task WaitUntilReadyAsync(RemoteTarget target, TimeSpan timeout, CancellationToken ct);
    Task<CommandResult> RunAsync(RemoteTarget target, string command, CancellationToken ct);
    Task UploadTextAsync(RemoteTarget target, string remotePath, string content, CancellationToken ct);
}
