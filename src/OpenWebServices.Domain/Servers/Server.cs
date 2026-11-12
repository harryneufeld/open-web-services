using System.Text.RegularExpressions;

namespace OpenWebServices.Domain.Servers;

/// <summary>A virtual server rented from a hosting provider and managed by the platform.</summary>
public sealed partial class Server
{
    private Server() { } // EF Core

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string ProviderId { get; private set; } = "";
    public long? ProviderServerId { get; private set; }
    public long? ProviderSshKeyId { get; private set; }
    public string Location { get; private set; } = "";
    public string ServerType { get; private set; } = "";
    public string Image { get; private set; } = "";
    public decimal MonthlyPriceNet { get; private set; }
    public string Currency { get; private set; } = "";
    public ServerStatus Status { get; private set; }
    public string? PublicIpv4 { get; private set; }
    public string EncryptedSshPrivateKey { get; private set; } = "";
    public string? FailureReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Server Provision(
        string name,
        string providerId,
        string location,
        string serverType,
        string image,
        decimal monthlyPriceNet,
        string currency,
        string encryptedSshPrivateKey,
        DateTime nowUtc)
    {
        if (!ValidName().IsMatch(name))
            throw new DomainException("Server name must be 1-63 characters: lowercase letters, digits and hyphens, not starting or ending with a hyphen.");
        RequireText(providerId, nameof(providerId));
        RequireText(location, nameof(location));
        RequireText(serverType, nameof(serverType));
        RequireText(image, nameof(image));
        RequireText(encryptedSshPrivateKey, nameof(encryptedSshPrivateKey));
        if (monthlyPriceNet < 0)
            throw new DomainException("Price cannot be negative.");

        return new Server
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = providerId,
            Location = location,
            ServerType = serverType,
            Image = image,
            MonthlyPriceNet = monthlyPriceNet,
            Currency = currency,
            EncryptedSshPrivateKey = encryptedSshPrivateKey,
            Status = ServerStatus.Provisioning,
            CreatedAtUtc = nowUtc,
        };
    }

    public bool IsReachable => Status == ServerStatus.Running && PublicIpv4 is not null;

    public void AttachProviderResources(long providerServerId, long providerSshKeyId, string? ipv4)
    {
        ProviderServerId = providerServerId;
        ProviderSshKeyId = providerSshKeyId;
        PublicIpv4 = ipv4;
    }

    public void MarkRunning(string? ipv4)
    {
        EnsureNotDeleting();
        Status = ServerStatus.Running;
        PublicIpv4 = ipv4 ?? PublicIpv4;
        FailureReason = null;
    }

    public void MarkStopped()
    {
        EnsureNotDeleting();
        Status = ServerStatus.Stopped;
    }

    public void MarkFailed(string reason)
    {
        Status = ServerStatus.Failed;
        FailureReason = reason;
    }

    public void MarkDeleting() => Status = ServerStatus.Deleting;

    private void EnsureNotDeleting()
    {
        if (Status == ServerStatus.Deleting)
            throw new DomainException("The server is being deleted.");
    }

    private static void RequireText(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{parameter} is required.");
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex ValidName();
}
