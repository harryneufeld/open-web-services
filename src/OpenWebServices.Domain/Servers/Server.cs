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
    public Guid TenantId { get; private set; }
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

    /// <summary>
    /// Private half of the key ows itself signs in with ("ows automation"), encrypted. Null when ows has no access,
    /// e.g. for imported servers or after the key was revoked.
    /// </summary>
    public string? EncryptedSshPrivateKey { get; private set; }

    /// <summary>Public half of the ows automation key, as an authorized_keys line.</summary>
    public string? AutomationPublicKey { get; private set; }

    public DateTime? AutomationKeyCreatedAtUtc { get; private set; }

    /// <summary>A replaced or revoked automation key that must still be removed from the server.</summary>
    public string? RetiredAutomationPublicKey { get; private set; }

    /// <summary>The server's SSH host key, pinned on first contact. Later connections must present the same key.</summary>
    public string? HostKeyAlgorithm { get; private set; }

    public string? HostKeyFingerprint { get; private set; }

    /// <summary>When ows last read or wrote the server's authorized_keys, successfully or not.</summary>
    public DateTime? AccessCheckedAtUtc { get; private set; }

    /// <summary>Why the last access check failed; null after a successful one.</summary>
    public string? AccessError { get; private set; }

    // authorized_keys lines ows does not know, one per line (lines never contain a line break).
    private string? UnknownKeysText { get; set; }

    private const char LineSeparator = '\n';
    public string? FailureReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? PendingSinceUtc { get; private set; }

    public static Server Provision(
        Guid tenantId,
        Guid groupId,
        string name,
        string location,
        string serverType,
        string image,
        decimal monthlyPriceNet,
        string currency,
        string encryptedSshPrivateKey,
        string automationPublicKey,
        DateTime nowUtc)
    {
        if (!ValidName().IsMatch(name))
            throw new DomainException("Server name must be 1-63 characters: lowercase letters, digits and hyphens, not starting or ending with a hyphen.");
        RequireText(encryptedSshPrivateKey, nameof(encryptedSshPrivateKey));
        RequireText(automationPublicKey, nameof(automationPublicKey));

        var server = Create(tenantId, groupId, name, location, serverType, image, monthlyPriceNet, currency, nowUtc);
        server.Origin = ServerOrigin.Created;
        server.EncryptedSshPrivateKey = encryptedSshPrivateKey;
        server.AutomationPublicKey = automationPublicKey;
        server.AutomationKeyCreatedAtUtc = nowUtc;
        server.Status = ServerStatus.Provisioning;
        return server;
    }

    /// <summary>Starts tracking a server that already exists at the provider.</summary>
    public static Server Import(
        Guid tenantId,
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

        var server = Create(tenantId, groupId, trimmed, location, serverType, image, monthlyPriceNet, currency, nowUtc);
        server.Origin = ServerOrigin.Imported;
        server.ExternalId = externalId;
        server.Status = ServerStatus.Stopped; // an unknown provider state shows as stopped rather than "busy" forever
        server.ApplyObserved(state, ipv4, nowUtc);
        return server;
    }

    private static Server Create(
        Guid tenantId, Guid groupId, string name, string location, string serverType, string image,
        decimal monthlyPriceNet, string currency, DateTime nowUtc)
    {
        if (tenantId == Guid.Empty || groupId == Guid.Empty)
            throw new DomainException("A tenant and a provider group are required.");
        RequireText(location, nameof(location));
        RequireText(serverType, nameof(serverType));
        if (monthlyPriceNet < 0)
            throw new DomainException("Price cannot be negative.");

        return new Server
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
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

    /// <summary>ows holds a key it can sign in with. Imported servers have none until a user gives ows access.</summary>
    public bool HasAutomationKey => EncryptedSshPrivateKey is not null;

    /// <summary>Apps are installed over SSH with the ows automation key.</summary>
    public bool CanInstallApps => IsReachable && HasAutomationKey;

    /// <summary>ows can read and change the server's authorized_keys now.</summary>
    public bool CanManageKeys => IsReachable && HasAutomationKey;

    public IReadOnlyList<string> UnknownAuthorizedKeys =>
        string.IsNullOrEmpty(UnknownKeysText) ? [] : UnknownKeysText.Split(LineSeparator);

    /// <summary>True while the server is expected to change state soon; syncs run more often then.</summary>
    public bool IsTransitioning => Status is ServerStatus.Provisioning or ServerStatus.Starting or ServerStatus.Stopping;

    public void AttachProviderResources(string externalId, string externalSshKeyId, string? ipv4)
    {
        ExternalId = externalId;
        ExternalSshKeyId = externalSshKeyId;
        PublicIpv4 = ipv4;
    }

    /// <summary>Stores the same private key re-encrypted, e.g. after a change of encryption format.</summary>
    public void ReplaceSshPrivateKey(string encryptedSshPrivateKey)
    {
        if (EncryptedSshPrivateKey is null)
            throw new DomainException("This server has no SSH key.");
        RequireText(encryptedSshPrivateKey, nameof(encryptedSshPrivateKey));
        EncryptedSshPrivateKey = encryptedSshPrivateKey;
    }

    /// <summary>Stores the public half of an existing automation key, e.g. for keys created before it was recorded.</summary>
    public void SetAutomationPublicKey(string publicKey)
    {
        if (!HasAutomationKey)
            throw new DomainException("This server has no ows automation key.");
        RequireText(publicKey, nameof(publicKey));
        AutomationPublicKey = publicKey;
    }

    /// <summary>
    /// Switches ows to a new automation key. The previous key is kept as retired until it is removed from the server.
    /// </summary>
    public void ReplaceAutomationKey(string encryptedPrivateKey, string publicKey, DateTime nowUtc)
    {
        RequireText(encryptedPrivateKey, nameof(encryptedPrivateKey));
        RequireText(publicKey, nameof(publicKey));
        if (AutomationPublicKey is { } previous && previous != publicKey)
            RetiredAutomationPublicKey = previous;
        EncryptedSshPrivateKey = encryptedPrivateKey;
        AutomationPublicKey = publicKey;
        AutomationKeyCreatedAtUtc = nowUtc;
    }

    /// <summary>ows forgets its key. Its public key stays retired until it is confirmed gone from the server.</summary>
    public void RevokeAutomationKey()
    {
        if (!HasAutomationKey)
            throw new DomainException("ows has no access to this server.");
        RetiredAutomationPublicKey = AutomationPublicKey ?? RetiredAutomationPublicKey;
        EncryptedSshPrivateKey = null;
        AutomationPublicKey = null;
        AutomationKeyCreatedAtUtc = null;
    }

    public void ClearRetiredAutomationKey() => RetiredAutomationPublicKey = null;

    /// <summary>The provider-side copy of the first automation key was deleted.</summary>
    public void ForgetProviderSshKey() => ExternalSshKeyId = null;

    /// <summary>Remembers the host key seen on first contact. A pinned key is never silently replaced.</summary>
    public void PinHostKey(string algorithm, string fingerprint)
    {
        RequireText(algorithm, nameof(algorithm));
        RequireText(fingerprint, nameof(fingerprint));
        if (HostKeyFingerprint is not null && HostKeyFingerprint != fingerprint)
            throw new DomainException("A different host key is already pinned for this server.");
        HostKeyAlgorithm = algorithm;
        HostKeyFingerprint = fingerprint;
    }

    /// <summary>Accepts whatever host key the server presents next, e.g. after a reinstall.</summary>
    public void ForgetHostKey()
    {
        HostKeyAlgorithm = null;
        HostKeyFingerprint = null;
    }

    public void RecordAccessCheck(DateTime nowUtc, IReadOnlyList<string> unknownKeys)
    {
        AccessCheckedAtUtc = nowUtc;
        AccessError = null;
        UnknownKeysText = unknownKeys.Count == 0
            ? null
            : string.Join(LineSeparator, unknownKeys.Select(k => k.ReplaceLineEndings(" ")));
    }

    /// <summary>Forgets the last check, so the next background check runs right away.</summary>
    public void ResetAccessCheck()
    {
        AccessCheckedAtUtc = null;
        AccessError = null;
    }

    public void RecordAccessFailure(DateTime nowUtc, string error)
    {
        AccessCheckedAtUtc = nowUtc;
        AccessError = error.Length > 2000 ? error[..2000] : error;
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
