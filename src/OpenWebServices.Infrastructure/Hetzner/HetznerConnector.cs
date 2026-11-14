using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Groups;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>
/// Hetzner Cloud. API tokens belong to one project, so each project is its own group.
/// Servers and DNS share the same token and API.
/// </summary>
internal sealed class HetznerConnector(IHttpClientFactory httpClients) : IProviderConnector
{
    public const string ProviderType = "hetzner";
    public const string HttpClientName = "hetzner";

    public ProviderTypeInfo Type { get; } = new(
        ProviderType,
        "Hetzner Cloud",
        "One group per Hetzner Cloud project. Create a token with Read & Write permission in the Cloud Console " +
        "under the project's Security → API tokens.",
        ResourceKind.Servers | ResourceKind.Dns,
        [new CredentialField(GroupCredentials.ApiTokenKey, "API token")]);

    public IProviderConnection Connect(IReadOnlyDictionary<string, string> credentials)
    {
        var token = credentials.GetValueOrDefault(GroupCredentials.ApiTokenKey);
        if (string.IsNullOrWhiteSpace(token))
            throw new ProviderException("The Hetzner API token is missing.");

        var api = new HetznerApiClient(httpClients.CreateClient(HttpClientName), token);
        return new Connection(Type, api);
    }

    private sealed class Connection(ProviderTypeInfo type, HetznerApiClient api) : IProviderConnection
    {
        public ProviderTypeInfo Type => type;
        public IServerProvider Servers { get; } = new HetznerServerProvider(api);
        public IDnsProvider Dns { get; } = new HetznerDnsProvider(api);
        public IServerNetworkProvider Networks { get; } = new HetznerNetworkProvider(api);
        public IFirewallProvider Firewalls { get; } = new HetznerFirewallProvider(api);

        // Hetzner Cloud has no domain registration API.
        public IDomainRegistrar? Domains => null;

        // Any authenticated call proves the token; listing one server is the cheapest.
        public Task VerifyAsync(CancellationToken ct) => api.GetRawAsync("servers?per_page=1", ct);
    }
}
