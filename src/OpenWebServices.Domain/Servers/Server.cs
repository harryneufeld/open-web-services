using System.Text.RegularExpressions;

namespace OpenWebServices.Domain.Servers;

/// <summary>A virtual server at a hosting provider, tracked by the platform. It belongs to one provider group.</summary>
public sealed partial class Server
{
    public const int MaxNameLength = 255;

    /// <summary>
    /// How long an optimistic Starting/Stopping state wins over a provider still reporting the old state.
    /// Providers accept power actions asynchronously, so the first syncs often still show the previous state.
    /// </summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(90);

    private Server() { } // EF Core

    public Guid Id { get; private set; }
    public Guid GroupId { get; private set; }
    public string Name { get; private set; } = "";
    public ServerOrigin Origin { get; private set; }
    public string? ExternalId { get; private set; }
    public string? ExternalSshKeyId { get; private set; }
    public string Location { get; private set; } = "";
    public string ServerType { get; private set; } = "";
    public string Image { get; private set; } = "";
    public decimal MonthlyPriceNet { get; private set; }
    public string Currency { get; private set; } = "";
    public ServerStatus Status { get; private set; }
    public string? PublicIpv4 { get; private set; }
    public string? EncryptedSshPrivateKey { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? PendingSinceUtc { get; private set; }

    public static Server Provision(
        Guid groupId,
        string name,
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
        RequireText(encryptedSshPrivateKey, nameof(encryptedSshPrivateKey));

        var server = Create(groupId, name, location, serverType, image, monthlyPriceNet, currency, nowUtc);
        server.Origin = ServerOrigin.Created;
        server.EncryptedSshPrivateKey = encryptedSshPrivateKey;
        server.Status = ServerStatus.Provisioning;
        return server;
    }

    /// <summary>Starts tracking a server that already exists at the provider.</summary>
    public static Server Import(
        Guid groupId,
        string externalId,
        string name,
        ObservedServerState state,
        string? ipv4,
        string location,
        string serverType,
        string image,
        decimal monthlyPriceNet,
        string currency,
        DateTime nowUtc)
    {
        RequireText(externalId, nameof(externalId));
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
            throw new DomainException($"Server name must be 1-{MaxNameLength} characters.");

        var server = Create(groupId, trimmed, location, serverType, image, monthlyPriceNet, currency, nowUtc);
        server.Origin = ServerOrigin.Imported;
        server.ExternalId = externalId;
        server.Status = ServerStatus.Stopped; // an unknown provider state shows as stopped rather than "busy" forever
        server.ApplyObserved(state, ipv4, nowUtc);
        return server;
    }

    private static Server Create(
        Guid groupId, string name, string location, string serverType, string image,
        decimal monthlyPriceNet, string currency, DateTime nowUtc)
    {
        if (groupId == Guid.Empty)
            throw new DomainException("A provider group is required.");
        RequireText(location, nameof(location));
        RequireText(serverType, nameof(serverType));
        if (monthlyPriceNet < 0)
            throw new DomainException("Price cannot be negative.");

        return new Server
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            Name = name,
            Location = location,
            ServerType = serverType,
            Image = string.IsNullOrWhiteSpace(image) ? "unknown" : image,
            MonthlyPriceNet = monthlyPriceNet,
            Currency = currency,
            CreatedAtUtc = nowUtc,
        };
    }

    public bool IsReachable => Status == ServerStatus.Running && PublicIpv4 is not null;

    /// <summary>Apps are installed over SSH with the key generated at creation, so imported servers cannot get apps.</summary>
    public bool CanInstallApps => IsReachable && EncryptedSshPrivateKey is not null;

    /// <summary>True while the server is expected to change state soon; syncs run more often then.</summary>
    public bool IsTransitioning => Status is ServerStatus.Provisioning or ServerStatus.Starting or ServerStatus.Stopping;

    public void AttachProviderResources(string externalId, string externalSshKeyId, string? ipv4)
    {
        ExternalId = externalId;
        ExternalSshKeyId = externalSshKeyId;
        PublicIpv4 = ipv4;
    }

    /// <summary>Optimistically shows a power action as in progress until the provider confirms the new state.</summary>
    public void MarkStarting(DateTime nowUtc) => BeginTransition(ServerStatus.Starting, nowUtc);

    public void MarkStopping(DateTime nowUtc) => BeginTransition(ServerStatus.Stopping, nowUtc);

    /// <summary>Reconciles the record with the state the provider reports.</summary>
    public void ApplyObserved(ObservedServerState state, string? ipv4, DateTime nowUtc)
    {
        if (Status == ServerStatus.Deleting)
            return;
        if (ipv4 is not null)
            PublicIpv4 = ipv4;

        var pending = PendingSinceUtc is { } since && nowUtc - since < PendingGrace;
        switch (state)
        {
            case ObservedServerState.Running when !(Status == ServerStatus.Stopping && pending):
                Settle(ServerStatus.Running);
                break;
            case ObservedServerState.Stopped when !(Status == ServerStatus.Starting && pending):
                Settle(ServerStatus.Stopped);
                break;
            case ObservedServerState.Starting when Status != ServerStatus.Provisioning:
                Status = ServerStatus.Starting;
                break;
            case ObservedServerState.Stopping:
                Status = ServerStatus.Stopping;
                break;
        }
    }

    public void MarkFailed(string reason)
    {
        Status = ServerStatus.Failed;
        FailureReason = reason;
        PendingSinceUtc = null;
    }

    public void MarkDeleting() => Status = ServerStatus.Deleting;

    private void BeginTransition(ServerStatus status, DateTime nowUtc)
    {
        if (Status == ServerStatus.Deleting)
            throw new DomainException("The server is being deleted.");
        Status = status;
        PendingSinceUtc = nowUtc;
    }

    private void Settle(ServerStatus status)
    {
        Status = status;
        FailureReason = null;
        PendingSinceUtc = null;
    }

    private static void RequireText(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{parameter} is required.");
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex ValidName();
}
