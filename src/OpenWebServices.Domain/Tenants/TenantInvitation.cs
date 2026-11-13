namespace OpenWebServices.Domain.Tenants;

/// <summary>
/// A single-use link that lets someone join a tenant with a given role. Only a hash of the link's token is stored,
/// so a database leak does not leak usable invitations.
/// </summary>
public sealed class TenantInvitation
{
    public const int MaxNoteLength = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private TenantInvitation() { } // EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public TenantRole Role { get; private set; }

    /// <summary>Who the link is meant for, e.g. a name or e-mail address. Informational only.</summary>
    public string Note { get; private set; } = "";

    public string TokenHash { get; private set; } = "";
    public Guid? CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? AcceptedAtUtc { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    public static TenantInvitation Create(
        Guid tenantId, TenantRole role, string note, string tokenHash, Guid? createdByUserId, DateTime nowUtc)
    {
        var trimmed = note.Trim();
        if (trimmed.Length > MaxNoteLength)
            throw new DomainException($"The note must be at most {MaxNoteLength} characters.");
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainException("A token hash is required.");

        return new TenantInvitation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Role = role,
            Note = trimmed,
            TokenHash = tokenHash,
            CreatedByUserId = createdByUserId,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + Lifetime,
        };
    }

    public bool IsUsable(DateTime nowUtc) => AcceptedAtUtc is null && RevokedAtUtc is null && nowUtc < ExpiresAtUtc;

    public void Accept(Guid userId, DateTime nowUtc)
    {
        if (!IsUsable(nowUtc))
            throw new DomainException("This invitation link has expired or was already used.");
        AcceptedAtUtc = nowUtc;
        AcceptedByUserId = userId;
    }

    public void Revoke(DateTime nowUtc)
    {
        if (AcceptedAtUtc is null && RevokedAtUtc is null)
            RevokedAtUtc = nowUtc;
    }
}
