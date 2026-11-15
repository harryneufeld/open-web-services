using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Groups;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud API v1 connector for Public Cloud instances and DNS records.</summary>
internal sealed class OvhConnector(IHttpClientFactory httpClients, ILogger<OvhApiClient> logger) : IProviderConnector
{
    public const string ProviderType = "ovh";
    public const string HttpClientName = "ovh";
    private const string ApplicationKey = "application_key";
    private const string ApplicationSecret = "application_secret";
    private const string ConsumerKey = "consumer_key";
    private const string EndpointKey = "endpoint";
    private const string ProjectIdKey = "cloud_project_id";
    private const string SubsidiaryKey = "ovh_subsidiary";

    private static readonly IReadOnlyDictionary<string, string> Endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ovh-eu"] = "https://eu.api.ovh.com/1.0/",
        ["ovh-ca"] = "https://ca.api.ovh.com/1.0/",
        ["ovh-us"] = "https://api.us.ovhcloud.com/1.0/",
    };

    public ProviderTypeInfo Type { get; } = new(
        ProviderType,
        "OVHcloud",
        "One group per Public Cloud project. Create an application key and secret, then a consumer key with the " +
        "access rules GET, POST, PUT and DELETE on /* (setup steps and a narrower alternative in docs/ovh.md). " +
        "Enable DNS and domain registrations on only one group per OVHcloud account. DNS zones must already exist " +
        "in OVHcloud; this connector manages their records.",
        ResourceKind.Servers | ResourceKind.Dns | ResourceKind.Domains,
        [
            new CredentialField(EndpointKey, "API endpoint (ovh-eu, ovh-ca, or ovh-us)", IsSecret: false,
                Help: "Choose the OVHcloud API region where your account is managed."),
            new CredentialField(ApplicationKey, "Application key"),
            new CredentialField(ApplicationSecret, "Application secret"),
            new CredentialField(ConsumerKey, "Consumer key"),
            new CredentialField(ProjectIdKey, "Public Cloud project ID", IsSecret: false,
                Help: "The Public Cloud project this group manages. Add one group per project."),
            new CredentialField(SubsidiaryKey, "OVHcloud subsidiary", IsSecret: false,
                Help: "Two-letter country code used for pricing, such as FR, DE, CA, or US."),
        ],
        HasCommitmentOffers: true);

    public IProviderConnection Connect(IReadOnlyDictionary<string, string> credentials)
    {
        var endpoint = credentials.GetValueOrDefault(EndpointKey)?.Trim();
        if (endpoint is null || !Endpoints.TryGetValue(endpoint, out var baseAddress))
            throw new ProviderException("Choose a supported OVHcloud API endpoint: ovh-eu, ovh-ca, or ovh-us.");

        var applicationKey = Required(credentials, ApplicationKey, "application key");
        var applicationSecret = Required(credentials, ApplicationSecret, "application secret");
        var consumerKey = Required(credentials, ConsumerKey, "consumer key");
        var projectId = Required(credentials, ProjectIdKey, "Public Cloud project ID");
        var subsidiary = Required(credentials, SubsidiaryKey, "subsidiary").ToUpperInvariant();
        if (subsidiary.Length != 2 || !subsidiary.All(char.IsAsciiLetter))
            throw new ProviderException("The OVHcloud subsidiary must be a two-letter country code, such as FR or DE.");
        var http = httpClients.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(baseAddress);

        var api = new OvhApiClient(http, logger, applicationKey, applicationSecret, consumerKey);
        return new Connection(Type, api, projectId, subsidiary, endpoint);
    }

    private static string Required(IReadOnlyDictionary<string, string> credentials, string key, string label) =>
        credentials.GetValueOrDefault(key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ProviderException($"The OVHcloud {label} is missing.");

    private sealed class Connection(ProviderTypeInfo type, OvhApiClient api, string projectId, string subsidiary, string endpoint) : IProviderConnection
    {
        public ProviderTypeInfo Type => type;
        public IServerProvider Servers { get; } = new OvhServerProvider(api, projectId, subsidiary, endpoint);
        public IDnsProvider Dns { get; } = new OvhDnsProvider(api);
        public IServerNetworkProvider Networks { get; } = new OvhNetworkProvider(api, projectId);

        // API v1 has no security groups for Public Cloud instances (see docs/ovh.md).
        public IFirewallProvider? Firewalls => null;
        public IDomainRegistrar Domains { get; } = new OvhDomainRegistrar(api, subsidiary);

        public Task VerifyAsync(CancellationToken ct) => api.GetRawAsync("me", ct);
    }
}
