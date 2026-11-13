using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Groups;

namespace OpenWebServices.Infrastructure.Providers;

/// <summary>
/// Every registered provider type. Add a provider by implementing <see cref="IProviderConnector"/> and
/// registering it in DependencyInjection; the UI and use cases pick it up from here.
/// </summary>
internal sealed class ProviderCatalog(IEnumerable<IProviderConnector> connectors) : IProviderCatalog
{
    private readonly Dictionary<string, IProviderConnector> _connectors = connectors.ToDictionary(c => c.Type.Id);

    public IReadOnlyList<ProviderTypeInfo> All => [.. _connectors.Values.Select(c => c.Type).OrderBy(t => t.DisplayName)];

    public ProviderTypeInfo? Find(string providerType) => _connectors.GetValueOrDefault(providerType)?.Type;

    public IProviderConnector Connector(string providerType) =>
        _connectors.GetValueOrDefault(providerType) ?? throw new NotFoundException($"Provider type '{providerType}'");
}

/// <summary>Opens connections for groups from their encrypted credentials.</summary>
internal sealed class ProviderConnectionFactory(
    ProviderCatalog catalog,
    IProviderGroupRepository groups,
    ISecretProtector secrets) : IProviderConnectionFactory
{
    public async Task<IProviderConnection> ConnectAsync(Guid groupId, CancellationToken ct)
    {
        var group = await groups.FindAsync(groupId, ct) ?? throw new NotFoundException("Provider group");
        if (!group.HasCredentials)
            throw new ProviderNotConfiguredException(group.Name);

        var credentials = GroupCredentials.Deserialize(secrets.Unprotect(group.EncryptedCredentials));
        return Connect(group.ProviderType, credentials);
    }

    public IProviderConnection Connect(string providerType, IReadOnlyDictionary<string, string> credentials) =>
        catalog.Connector(providerType).Connect(credentials);
}
