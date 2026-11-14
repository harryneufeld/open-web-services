using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Application.Abstractions;

/// <summary>One credential input a provider type needs, e.g. an API token or an access key id.</summary>
public sealed record CredentialField(string Key, string Label, bool IsSecret = true, string? Help = null);

/// <summary>
/// Describes a provider type (Hetzner Cloud, AWS, OVH, ...): what it can manage and which credentials a group
/// of this type needs. The UI renders the "add group" form from this, so new providers need no UI changes.
/// </summary>
/// <param name="HasCommitmentOffers">
/// The provider also lists offers with a billing commitment (see <see cref="OfferBilling"/>), so groups can hide them.
/// </param>
public sealed record ProviderTypeInfo(
    string Id,
    string DisplayName,
    string Description,
    ResourceKind SupportedResources,
    IReadOnlyList<CredentialField> CredentialFields,
    bool HasCommitmentOffers = false);

/// <summary>A live, authenticated connection to one provider group. Capabilities are null when unsupported.</summary>
public interface IProviderConnection
{
    ProviderTypeInfo Type { get; }

    IServerProvider? Servers { get; }

    IDnsProvider? Dns { get; }

    /// <summary>Checks that the credentials are accepted. Throws <see cref="ProviderException"/> otherwise.</summary>
    Task VerifyAsync(CancellationToken ct);
}

/// <summary>Plug-in point for a provider type. Implement one per provider in Infrastructure and register it.</summary>
public interface IProviderConnector
{
    ProviderTypeInfo Type { get; }

    IProviderConnection Connect(IReadOnlyDictionary<string, string> credentials);
}

public interface IProviderCatalog
{
    IReadOnlyList<ProviderTypeInfo> All { get; }

    ProviderTypeInfo? Find(string providerType);
}

public interface IProviderConnectionFactory
{
    /// <exception cref="NotFoundException">The group does not exist.</exception>
    /// <exception cref="ProviderNotConfiguredException">The group has no usable credentials.</exception>
    Task<IProviderConnection> ConnectAsync(Guid groupId, CancellationToken ct);

    /// <summary>Connects with credentials that are not stored yet, e.g. to verify them before saving.</summary>
    IProviderConnection Connect(string providerType, IReadOnlyDictionary<string, string> credentials);
}

/// <summary>Virtual servers at a provider.</summary>
public interface IServerProvider
{
    Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct);
    Task<IReadOnlyList<ProviderServer>> ListServersAsync(CancellationToken ct);
    Task<ProviderServer?> GetServerAsync(string externalId, CancellationToken ct);
    Task<string> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct);
    Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct);
    Task PowerAsync(string externalId, PowerAction action, CancellationToken ct);
    Task DeleteServerAsync(string externalId, CancellationToken ct);
    Task DeleteSshKeyAsync(string externalSshKeyId, CancellationToken ct);
}

/// <summary>Authoritative DNS hosting at a provider.</summary>
public interface IDnsProvider
{
    Task<IReadOnlyList<ProviderZone>> ListZonesAsync(CancellationToken ct);
    Task<ProviderZone> CreateZoneAsync(string name, CancellationToken ct);
    Task DeleteZoneAsync(string zoneExternalId, CancellationToken ct);
    Task<IReadOnlyList<DnsRecordSet>> ListRecordSetsAsync(string zoneExternalId, CancellationToken ct);
    Task CreateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct);

    /// <summary>Replaces the values and TTL of an existing record set.</summary>
    Task UpdateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct);

    Task DeleteRecordSetAsync(string zoneExternalId, string name, string type, CancellationToken ct);
}

public static class ProviderConnectionExtensions
{
    public static IServerProvider RequireServers(this IProviderConnection connection) =>
        connection.Servers ?? throw new CapabilityNotSupportedException(connection.Type.DisplayName, ResourceKind.Servers);

    public static IDnsProvider RequireDns(this IProviderConnection connection) =>
        connection.Dns ?? throw new CapabilityNotSupportedException(connection.Type.DisplayName, ResourceKind.Dns);
}
