using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Abstractions;

/// <summary>A sign-in account, as the application sees it. Credentials stay in the identity store.</summary>
public sealed record UserSummary(
    Guid Id,
    string UserName,
    bool IsOwsAdmin,
    bool HasAuthenticator,
    int PasskeyCount,
    bool IsLockedOut,
    Guid? CurrentTenantId,
    DateTime CreatedAtUtc)
{
    public bool HasSecondFactor => HasAuthenticator || PasskeyCount > 0;
}

/// <summary>Reads and changes accounts. Every change that affects access rotates the user's security stamp.</summary>
public interface IUserDirectory
{
    Task<bool> AnyAsync(CancellationToken ct);
    Task<UserSummary?> FindAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<UserSummary>> ListAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);
    Task SetCurrentTenantAsync(Guid userId, Guid? tenantId, CancellationToken ct);
    Task SetOwsAdminAsync(Guid userId, bool isOwsAdmin, CancellationToken ct);
    Task SetLockedAsync(Guid userId, bool locked, CancellationToken ct);

    /// <summary>Removes the authenticator, recovery codes and passkeys, e.g. after a lost phone.</summary>
    Task ResetSecondFactorsAsync(Guid userId, CancellationToken ct);
}

public interface ITenantRepository
{
    void Add(Tenant tenant);
    Task<Tenant?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
}

public interface ITenantMembershipRepository
{
    void Add(TenantMembership membership);
    void Remove(TenantMembership membership);
    Task<TenantMembership?> FindAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TenantMembership>> ListByTenantAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<TenantMembership>> ListByUserAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TenantMembership>> ListAllAsync(CancellationToken ct);
}

public interface ITenantInvitationRepository
{
    void Add(TenantInvitation invitation);
    Task<TenantInvitation?> FindAsync(Guid id, CancellationToken ct);
    Task<TenantInvitation?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);
    Task<IReadOnlyList<TenantInvitation>> ListOpenAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct);
}

/// <summary>A security-relevant event: sign-ins, second-factor changes, roles, invitations, credentials.</summary>
public sealed record AuditEntry(
    string Type,
    string Detail,
    Guid? ActorUserId = null,
    Guid? TenantId = null,
    string? IpAddress = null);

public sealed record AuditRecord(
    DateTime AtUtc, string Type, string Detail, Guid? ActorUserId, Guid? TenantId, string? IpAddress);

/// <summary>Append-only security log. Entries are stored with the next unit-of-work save.</summary>
public interface IAuditLog
{
    void Add(AuditEntry entry);
    Task<IReadOnlyList<AuditRecord>> ListRecentAsync(int count, CancellationToken ct);
}

public static class AuditTypes
{
    public const string SignIn = "sign-in";
    public const string SignInFailed = "sign-in-failed";
    public const string LockedOut = "locked-out";
    public const string SignOut = "sign-out";
    public const string Setup = "setup";
    public const string PasswordChanged = "password-changed";
    public const string AuthenticatorEnabled = "authenticator-enabled";
    public const string AuthenticatorDisabled = "authenticator-disabled";
    public const string RecoveryCodesRenewed = "recovery-codes-renewed";
    public const string RecoveryCodeUsed = "recovery-code-used";
    public const string PasskeyAdded = "passkey-added";
    public const string PasskeyRemoved = "passkey-removed";
    public const string TenantCreated = "tenant-created";
    public const string TenantSettingsChanged = "tenant-settings-changed";
    public const string InvitationCreated = "invitation-created";
    public const string InvitationRevoked = "invitation-revoked";
    public const string InvitationAccepted = "invitation-accepted";
    public const string MemberRoleChanged = "member-role-changed";
    public const string MemberRemoved = "member-removed";
    public const string OwsAdminChanged = "ows-admin-changed";
    public const string UserLocked = "user-locked";
    public const string UserUnlocked = "user-unlocked";
    public const string SecondFactorsReset = "second-factors-reset";
    public const string ProviderCredentialsChanged = "provider-credentials-changed";
    public const string ProviderGroupRemoved = "provider-group-removed";
    public const string DomainOrdered = "domain-ordered";
    public const string DomainCancelled = "domain-cancelled";
    public const string DomainCancellationWithdrawn = "domain-cancellation-withdrawn";
}
