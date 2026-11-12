using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>Builds provider clients from the stored (encrypted) credentials. New providers are added here.</summary>
internal sealed class HostingProviderFactory(
    IProviderAccountRepository accounts,
    ISecretProtector secrets,
    IHttpClientFactory httpClients) : IHostingProviderFactory
{
    public const string HetznerHttpClientName = "hetzner";

    public IReadOnlyCollection<string> ProviderIds { get; } = [HetznerProvider.ProviderId];

    public async Task<IHostingProvider> GetAsync(string providerId, CancellationToken ct)
    {
        var account = await accounts.FindByProviderAsync(providerId, ct)
                      ?? throw new ProviderNotConfiguredException(providerId);
        var token = secrets.Unprotect(account.EncryptedApiToken);

        return providerId switch
        {
            HetznerProvider.ProviderId =>
                new HetznerProvider(new HetznerApiClient(httpClients.CreateClient(HetznerHttpClientName), token)),
            _ => throw new NotFoundException($"Provider '{providerId}'"),
        };
    }
}
