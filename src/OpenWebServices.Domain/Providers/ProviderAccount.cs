namespace OpenWebServices.Domain.Providers;

/// <summary>Credentials for one hosting provider. The token is stored encrypted.</summary>
public sealed class ProviderAccount
{
    private ProviderAccount() { } // EF Core

    public Guid Id { get; private set; }
    public string ProviderId { get; private set; } = "";
    public string EncryptedApiToken { get; private set; } = "";
    public DateTime UpdatedAtUtc { get; private set; }

    public static ProviderAccount Create(string providerId, string encryptedApiToken, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new DomainException("A provider is required.");
        if (string.IsNullOrWhiteSpace(encryptedApiToken))
            throw new DomainException("An API token is required.");

        return new ProviderAccount
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            EncryptedApiToken = encryptedApiToken,
            UpdatedAtUtc = nowUtc,
        };
    }

    public void ReplaceToken(string encryptedApiToken, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(encryptedApiToken))
            throw new DomainException("An API token is required.");
        EncryptedApiToken = encryptedApiToken;
        UpdatedAtUtc = nowUtc;
    }
}
