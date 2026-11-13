namespace OpenWebServices.Domain.Providers;

/// <summary>
/// One set of credentials at one provider, e.g. a Hetzner Cloud project token, an AWS account/role or an OVH
/// application. Every synced resource belongs to exactly one group. Credentials are stored encrypted, as an opaque
/// blob whose fields are defined by the provider type.
/// </summary>
public sealed class ProviderGroup
{
    public const int MaxNameLength = 100;
    public const int MaxSyncErrorLength = 2000;

    private ProviderGroup() { } // EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = "";
    public string ProviderType { get; private set; } = "";
    public string EncryptedCredentials { get; private set; } = "";
    public ResourceKind EnabledResources { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? LastSyncAtUtc { get; private set; }
    public string? LastSyncError { get; private set; }

    public bool HasCredentials => !string.IsNullOrEmpty(EncryptedCredentials);

    public static ProviderGroup Create(
        Guid tenantId, string name, string providerType, string encryptedCredentials, ResourceKind enabledResources, DateTime nowUtc)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("A tenant is required.");
        if (string.IsNullOrWhiteSpace(providerType))
            throw new DomainException("A provider type is required.");
        RequireCredentials(encryptedCredentials);

        var group = new ProviderGroup
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderType = providerType,
            EncryptedCredentials = encryptedCredentials,
            EnabledResources = enabledResources,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        group.Rename(name, nowUtc);
        return group;
    }

    public bool IsEnabled(ResourceKind kind) => EnabledResources.HasFlag(kind);

    public void Rename(string name, DateTime nowUtc)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
            throw new DomainException($"Group name must be 1-{MaxNameLength} characters.");
        Name = trimmed;
        UpdatedAtUtc = nowUtc;
    }

    public void ReplaceCredentials(string encryptedCredentials, DateTime nowUtc)
    {
        RequireCredentials(encryptedCredentials);
        EncryptedCredentials = encryptedCredentials;
        UpdatedAtUtc = nowUtc;
    }

    public void SetEnabledResources(ResourceKind kinds, DateTime nowUtc)
    {
        EnabledResources = kinds;
        UpdatedAtUtc = nowUtc;
    }

    public void RecordSyncSuccess(DateTime nowUtc)
    {
        LastSyncAtUtc = nowUtc;
        LastSyncError = null;
    }

    public void RecordSyncFailure(string error, DateTime nowUtc)
    {
        LastSyncAtUtc = nowUtc;
        LastSyncError = error.Length > MaxSyncErrorLength ? error[..MaxSyncErrorLength] : error;
    }

    private static void RequireCredentials(string encryptedCredentials)
    {
        if (string.IsNullOrWhiteSpace(encryptedCredentials))
            throw new DomainException("Credentials are required.");
    }
}
