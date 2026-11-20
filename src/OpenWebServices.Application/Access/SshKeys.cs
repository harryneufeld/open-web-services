using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.SshKeys;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Access;

public sealed record SshKeyView(
    Guid Id,
    string Name,
    Guid? OwnerUserId,
    string OwnerName,
    bool IsMine,
    string Fingerprint,
    string Algorithm,
    string PublicKey,
    SshKeyOrigin Origin,
    DateTime CreatedAtUtc,
    int ServerCount,
    bool CanRevoke)
{
    public bool IsShared => OwnerUserId is null;
}

public sealed record KeyOption(Guid Id, string Name, string Owner, string Algorithm, string Fingerprint, bool IsMine);

public sealed record SshActivity(DateTime AtUtc, string Detail, string Actor);

public sealed record SshKeysPage(
    IReadOnlyList<SshKeyView> Mine,
    IReadOnlyList<SshKeyView> Others,
    IReadOnlyList<SshActivity> Activity,
    bool CanAddOwn,
    bool CanAddShared);

/// <summary>Public keys people sign in to servers with. ows never sees or stores their private keys.</summary>
public sealed class SshKeys(
    AccessContext access,
    ISshKeyRepository keys,
    IServerKeyGrantRepository grants,
    IUserDirectory users,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const int ActivityCount = 30;

    public async Task<SshKeysPage> GetAsync(CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Viewer);
        var userId = access.RequireUser();
        var all = (await keys.ListAsync(ct)).Where(k => !k.IsRevoked).ToList();
        var names = await UserNamesAsync(all.Select(k => k.OwnerUserId), ct);

        var views = new List<SshKeyView>();
        foreach (var key in all.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var serverCount = (await grants.ListForKeyAsync(key.Id, ct)).Count(g => g.State == GrantState.Present);
            views.Add(new SshKeyView(
                key.Id, key.Name, key.OwnerUserId,
                key.OwnerUserId is { } owner ? names.GetValueOrDefault(owner, "former member") : "Shared",
                key.OwnerUserId == userId, key.Fingerprint, key.Algorithm, key.PublicKey, key.Origin, key.CreatedAtUtc,
                serverCount, CanManage(key, userId)));
        }

        var records = await audit.ListRecentAsync(tenantId, SshAuditTypes.All.ToList(), ActivityCount, ct);
        var actors = await UserNamesAsync(records.Select(r => r.ActorUserId), ct);
        var activity = records
            .Select(r => new SshActivity(r.AtUtc, r.Detail, r.ActorUserId is { } a ? actors.GetValueOrDefault(a, "former member") : "ows"))
            .ToList();

        return new SshKeysPage(
            views.Where(v => v.IsMine).ToList(),
            views.Where(v => !v.IsMine).OrderBy(v => !v.IsShared).ThenBy(v => v.OwnerName).ToList(),
            activity,
            access.Can(TenantRole.Member),
            access.Can(TenantRole.Admin));
    }

    /// <summary>Keys the current user may give servers access with: their own and the shared ones (all for admins).</summary>
    public async Task<IReadOnlyList<SshKey>> ListUsableAsync(CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var userId = access.RequireUser();
        return [.. (await keys.ListAsync(ct))
            .Where(k => !k.IsRevoked && (k.IsShared || k.OwnerUserId == userId || access.Can(TenantRole.Admin)))
            .OrderBy(k => k.OwnerUserId != userId).ThenBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Keys to offer when creating a server; the user's own keys are suggested.</summary>
    public async Task<IReadOnlyList<KeyOption>> ListOptionsAsync(CancellationToken ct)
    {
        var userId = access.RequireUser();
        var usable = await ListUsableAsync(ct);
        var names = await UserNamesAsync(usable.Select(k => k.OwnerUserId), ct);
        return [.. usable.Select(k => new KeyOption(k.Id, k.Name,
            k.IsShared ? "Shared" : k.OwnerUserId == userId ? "You" : names.GetValueOrDefault(k.OwnerUserId!.Value, "former member"),
            k.Algorithm, k.Fingerprint, k.OwnerUserId == userId))];
    }

    /// <param name="shared">A tenant key without owner, e.g. for a deploy pipeline. Needs the Admin role.</param>
    public async Task<Guid> AddAsync(string name, string publicKey, SshKeyOrigin origin, bool shared, CancellationToken ct)
    {
        var tenantId = access.Require(shared ? TenantRole.Admin : TenantRole.Member);
        var userId = access.RequireUser();
        if (origin == SshKeyOrigin.Adopted)
            throw new DomainException("Adopt keys from the server's SSH access panel.");

        var parsed = SshPublicKey.Parse(publicKey);
        await EnsureNewAsync(parsed, ct);
        var key = SshKey.Add(tenantId, shared ? null : userId, name, parsed, origin, userId, clock.UtcNow);
        keys.Add(key);
        audit.Add(new AuditEntry(SshAuditTypes.KeyAdded,
            $"Added {(shared ? "shared" : "personal")} key '{key.Name}' ({key.Algorithm} {key.Fingerprint})" +
            (origin == SshKeyOrigin.GeneratedInBrowser ? ", generated in the browser" : ""),
            userId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
        return key.Id;
    }

    /// <summary>Revokes a key: it can no longer be used and ows removes it from every server.</summary>
    public async Task RevokeAsync(Guid keyId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Member);
        var userId = access.RequireUser();
        var key = await keys.FindAsync(keyId, ct) ?? throw new NotFoundException("SSH key");
        if (!CanManage(key, userId))
            throw new ForbiddenException("Only admins can revoke shared keys and other members' keys.");

        var serverCount = await RevokeWithGrantsAsync(key, ct);
        audit.Add(new AuditEntry(SshAuditTypes.KeyRevoked,
            $"Revoked key '{key.Name}' ({key.Fingerprint}); removing it from {serverCount} server(s)", userId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>Revokes every personal key of a member, e.g. when they leave the tenant. Saved by the caller.</summary>
    public static async Task<int> RevokeAllOfAsync(
        ISshKeyRepository keys, IServerKeyGrantRepository grants, Guid tenantId, Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var owned = (await keys.ListByOwnerAsync(tenantId, userId, ct)).Where(k => !k.IsRevoked).ToList();
        foreach (var key in owned)
        {
            key.Revoke(nowUtc);
            foreach (var grant in await grants.ListForKeyAsync(key.Id, ct))
                grant.RequestRemoval();
        }
        return owned.Count;
    }

    private async Task<int> RevokeWithGrantsAsync(SshKey key, CancellationToken ct)
    {
        key.Revoke(clock.UtcNow);
        var keyGrants = await grants.ListForKeyAsync(key.Id, ct);
        foreach (var grant in keyGrants)
            grant.RequestRemoval();
        return keyGrants.Count;
    }

    private async Task EnsureNewAsync(SshPublicKey parsed, CancellationToken ct)
    {
        var existing = (await keys.ListAsync(ct)).FirstOrDefault(k => !k.IsRevoked && k.Fingerprint == parsed.Fingerprint);
        if (existing is not null)
            throw new DomainException($"This key is already in ows as '{existing.Name}'.");
    }

    private bool CanManage(SshKey key, Guid userId) =>
        access.Can(TenantRole.Admin) || (key.OwnerUserId == userId && access.Can(TenantRole.Member));

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var distinct = ids.OfType<Guid>().Distinct().ToList();
        return distinct.Count == 0 ? [] : (await users.ListAsync(distinct, ct)).ToDictionary(u => u.Id, u => u.UserName);
    }
}
