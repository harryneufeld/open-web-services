namespace OpenWebServices.Domain.SshKeys;

/// <summary>How a key came to ows. ows only ever stores the public half of these keys.</summary>
public enum SshKeyOrigin
{
    /// <summary>The user pasted the public key of a key pair they own.</summary>
    Pasted = 0,

    /// <summary>Generated in the user's browser; the private key was downloaded and never sent to ows.</summary>
    GeneratedInBrowser = 1,

    /// <summary>Found on a server and taken over as a known key.</summary>
    Adopted = 2,
}

/// <summary>
/// A public key people use to sign in to servers. A personal key belongs to one member; a shared key (no owner)
/// belongs to the tenant, e.g. for a deploy pipeline. Revoking a key removes it from every server.
/// </summary>
public sealed class SshKey
{
    public const int MaxNameLength = 64;

    private SshKey() { } // EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>The member the key belongs to; null for a shared tenant key.</summary>
    public Guid? OwnerUserId { get; private set; }

    public string Name { get; private set; } = "";

    /// <summary>"type base64", without comment.</summary>
    public string PublicKey { get; private set; } = "";

    public string Fingerprint { get; private set; } = "";
    public string Algorithm { get; private set; } = "";
    public SshKeyOrigin Origin { get; private set; }
    public Guid? CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Set when the key was revoked. It stays on record so ows can spot and remove leftover copies.</summary>
    public DateTime? RevokedAtUtc { get; private set; }

    public bool IsShared => OwnerUserId is null;
    public bool IsRevoked => RevokedAtUtc is not null;

    public SshPublicKey Key => SshPublicKey.Parse(PublicKey);

    public static SshKey Add(
        Guid tenantId, Guid? ownerUserId, string name, SshPublicKey key, SshKeyOrigin origin, Guid? createdByUserId, DateTime nowUtc)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("A tenant is required.");
        return new SshKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            Name = ValidName(name),
            PublicKey = $"{key.Type} {key.Blob}",
            Fingerprint = key.Fingerprint,
            Algorithm = key.Algorithm,
            Origin = origin,
            CreatedByUserId = createdByUserId,
            CreatedAtUtc = nowUtc,
        };
    }

    public void Revoke(DateTime nowUtc)
    {
        if (RevokedAtUtc is null)
            RevokedAtUtc = nowUtc;
    }

    /// <summary>The authorized_keys line ows writes: the key, labelled with its name.</summary>
    public string ToAuthorizedKeysLine() => Key.ToLine($"{Name} (ows)");

    public static string ValidName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new DomainException("Give the key a name, e.g. the device it lives on: laptop-2026.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Key names can be at most {MaxNameLength} characters.");
        if (trimmed.Any(char.IsControl))
            throw new DomainException("Key names must be a single line of text.");
        return trimmed;
    }
}

/// <summary>Whether a key should be on a server.</summary>
public enum GrantState
{
    /// <summary>The key should be in the server's authorized_keys.</summary>
    Present = 0,

    /// <summary>The key should be removed; the grant disappears once ows has removed it.</summary>
    Absent = 1,
}

/// <summary>
/// Access of one key to one server. ows writes the desired state to the server over SSH;
/// <see cref="AppliedAtUtc"/> says when the server last matched it.
/// </summary>
public sealed class ServerKeyGrant
{
    private ServerKeyGrant() { } // EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ServerId { get; private set; }
    public Guid SshKeyId { get; private set; }
    public GrantState State { get; private set; }
    public Guid? GrantedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>When the server was last seen in the desired state; null while a change is waiting.</summary>
    public DateTime? AppliedAtUtc { get; private set; }

    public bool IsPending => AppliedAtUtc is null;

    public static ServerKeyGrant Grant(Guid tenantId, Guid serverId, Guid sshKeyId, Guid? grantedByUserId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ServerId = serverId,
        SshKeyId = sshKeyId,
        State = GrantState.Present,
        GrantedByUserId = grantedByUserId,
        CreatedAtUtc = nowUtc,
    };

    /// <summary>Asks for the key to be added again after a removal was requested.</summary>
    public void Restore()
    {
        if (State == GrantState.Present)
            return;
        State = GrantState.Present;
        AppliedAtUtc = null;
    }

    public void RequestRemoval()
    {
        if (State == GrantState.Absent)
            return;
        State = GrantState.Absent;
        AppliedAtUtc = null;
    }

    public void MarkApplied(DateTime nowUtc) => AppliedAtUtc = nowUtc;
}
