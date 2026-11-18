using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.SshKeys;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Access;

public sealed record ServerKeyRow(
    Guid KeyId,
    string Name,
    string OwnerName,
    string Algorithm,
    string Fingerprint,
    GrantState State,
    bool IsPending,
    bool CanRemove);

public sealed record UnknownKeyRow(string Algorithm, string Fingerprint, string Comment);

public sealed record KeyChoice(Guid Id, string Label);

public sealed record AutomationKeyView(string PublicKey, string Algorithm, string Fingerprint, DateTime? CreatedAtUtc);

public sealed record ServerAccessView(
    Guid ServerId,
    string? Host,
    bool IsReachable,
    AutomationKeyView? Automation,
    bool RetiredKeyPending,
    HostKey? PinnedHostKey,
    DateTime? CheckedAtUtc,
    string? Error,
    IReadOnlyList<ServerKeyRow> Keys,
    IReadOnlyList<UnknownKeyRow> UnknownKeys,
    IReadOnlyList<KeyChoice> Addable,
    bool CanEdit,
    bool CanAdmin)
{
    public bool HasPendingChanges => RetiredKeyPending || Keys.Any(k => k.IsPending);
}

/// <summary>Who can sign in to a server, and the ows key itself: list, grant, remove, rotate, revoke, restore.</summary>
public sealed class ServerAccess(
    AccessContext access,
    IServerRepository servers,
    IServerKeyGrantRepository grants,
    ISshKeyRepository keys,
    SshKeys sshKeys,
    ServerAccessWriter writer,
    ServerAccessLocks locks,
    IRemoteShell shell,
    ISshKeyGenerator keyGenerator,
    ISecretProtector secrets,
    IProviderConnectionFactory connections,
    IUserDirectory users,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<ServerAccessView> GetAsync(Guid serverId, CancellationToken ct)
    {
        access.Require(TenantRole.Viewer);
        var userId = access.RequireUser();
        var server = await FindAsync(serverId, ct);
        var serverGrants = await grants.ListForServerAsync(serverId, ct);
        var allKeys = (await keys.ListAsync(ct)).ToDictionary(k => k.Id);
        var ownerIds = allKeys.Values.Select(k => k.OwnerUserId).OfType<Guid>().Distinct().ToList();
        var names = ownerIds.Count == 0 ? [] : (await users.ListAsync(ownerIds, ct)).ToDictionary(u => u.Id, u => u.UserName);

        var rows = serverGrants
            .Where(g => allKeys.ContainsKey(g.SshKeyId))
            .Select(g =>
            {
                var key = allKeys[g.SshKeyId];
                return new ServerKeyRow(key.Id, key.Name, OwnerName(key, names), key.Algorithm, key.Fingerprint, g.State, g.IsPending,
                    g.State == GrantState.Present && CanUse(key, userId));
            })
            .OrderBy(r => r.State).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var granted = serverGrants.Select(g => g.SshKeyId).ToHashSet();
        var addable = access.Can(TenantRole.Member)
            ? (await sshKeys.ListUsableAsync(ct)).Where(k => !granted.Contains(k.Id))
                .Select(k => new KeyChoice(k.Id, k.IsShared ? $"{k.Name} (shared)" : k.OwnerUserId == userId ? k.Name : $"{k.Name} ({OwnerName(k, names)})"))
                .ToList()
            : [];

        var unknown = server.UnknownAuthorizedKeys
            .Select(SshPublicKey.FindInAuthorizedKeysLine).OfType<SshPublicKey>()
            .Select(k => new UnknownKeyRow(k.Algorithm, k.Fingerprint, k.Comment))
            .ToList();

        AutomationKeyView? automation = null;
        if (server.AutomationPublicKey is { } automationKey && SshPublicKey.FindInAuthorizedKeysLine(automationKey) is { } parsed)
            automation = new AutomationKeyView(parsed.ToLine(ServerAccessWriter.AutomationLabel), parsed.Algorithm, parsed.Fingerprint, server.AutomationKeyCreatedAtUtc);

        return new ServerAccessView(
            server.Id, server.PublicIpv4, server.IsReachable, automation, server.RetiredAutomationPublicKey is not null,
            server.HostKeyFingerprint is { } fp ? new HostKey(server.HostKeyAlgorithm ?? "", fp) : null,
            server.AccessCheckedAtUtc, server.AccessError, rows, unknown, addable,
            access.Can(TenantRole.Member), access.Can(TenantRole.Admin));
    }

    /// <summary>Gives a key access to a server and writes it right away when the server is reachable.</summary>
    public async Task GrantAsync(Guid serverId, Guid keyId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Member);
        var userId = access.RequireUser();
        var server = await FindAsync(serverId, ct);
        if (!server.HasAutomationKey)
            throw new DomainException("ows has no access to this server, so it cannot add keys. Give ows access first.");
        var key = await keys.FindAsync(keyId, ct) ?? throw new NotFoundException("SSH key");
        if (key.IsRevoked)
            throw new DomainException("This key was revoked.");
        if (!CanUse(key, userId))
            throw new ForbiddenException("Only admins can give other members' keys access.");

        var existing = (await grants.ListForServerAsync(serverId, ct)).FirstOrDefault(g => g.SshKeyId == keyId);
        if (existing is null)
            grants.Add(ServerKeyGrant.Grant(tenantId, serverId, keyId, userId, clock.UtcNow));
        else
            existing.Restore();
        audit.Add(new AuditEntry(SshAuditTypes.AccessGranted, $"Gave key '{key.Name}' access to {server.Name}", userId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
        await ApplyNowAsync(server, ct);
    }

    public async Task RemoveAsync(Guid serverId, Guid keyId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Member);
        var userId = access.RequireUser();
        var server = await FindAsync(serverId, ct);
        var key = await keys.FindAsync(keyId, ct) ?? throw new NotFoundException("SSH key");
        if (!CanUse(key, userId))
            throw new ForbiddenException("Only admins can remove other members' keys.");
        var grant = (await grants.ListForServerAsync(serverId, ct)).FirstOrDefault(g => g.SshKeyId == keyId)
                    ?? throw new NotFoundException("Access");

        grant.RequestRemoval();
        audit.Add(new AuditEntry(SshAuditTypes.AccessRemoved, $"Removed key '{key.Name}' from {server.Name}", userId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
        await ApplyNowAsync(server, ct);
    }

    /// <summary>Reads the server's authorized_keys, applies waiting changes and lists keys ows does not know.</summary>
    public async Task CheckAsync(Guid serverId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var server = await FindAsync(serverId, ct);
        RequireManageable(server);
        await ApplyAsync(server, extraRemove: [], ct);
    }

    /// <summary>Removes a key ows found on the server but does not manage.</summary>
    public async Task RemoveUnknownAsync(Guid serverId, string fingerprint, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Member);
        var server = await FindAsync(serverId, ct);
        RequireManageable(server);
        var key = FindUnknown(server, fingerprint);

        await ApplyAsync(server, [key.Blob], ct);
        audit.Add(new AuditEntry(SshAuditTypes.UnknownKeyRemoved,
            $"Removed unknown key {key.Algorithm} {key.Fingerprint}{CommentSuffix(key)} from {server.Name}", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>Takes a key found on the server over as a shared key with access to this server.</summary>
    public async Task AdoptUnknownAsync(Guid serverId, string fingerprint, string name, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var server = await FindAsync(serverId, ct);
        RequireManageable(server);
        var found = FindUnknown(server, fingerprint);
        if ((await keys.ListAsync(ct)).FirstOrDefault(k => !k.IsRevoked && k.Fingerprint == found.Fingerprint) is { } known)
            throw new DomainException($"This key is already in ows as '{known.Name}'.");

        var key = SshKey.Add(tenantId, null, name, found, SshKeyOrigin.Adopted, access.UserId, clock.UtcNow);
        keys.Add(key);
        grants.Add(ServerKeyGrant.Grant(tenantId, serverId, key.Id, access.UserId, clock.UtcNow));
        audit.Add(new AuditEntry(SshAuditTypes.UnknownKeyAdopted,
            $"Adopted key {found.Fingerprint} on {server.Name} as shared key '{key.Name}'", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
        await ApplyNowAsync(server, ct);
    }

    /// <summary>
    /// Replaces the ows key: adds a new one, proves ows can sign in with it, then removes the old one. If any step
    /// fails before the proof, the old key stays in use and nothing is lost.
    /// </summary>
    public async Task RotateAutomationKeyAsync(Guid serverId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Member);
        var server = await FindAsync(serverId, ct);
        RequireManageable(server);

        using (await locks.AcquireAsync(serverId, ct))
        {
            var oldTarget = writer.TargetFor(server);
            var pair = keyGenerator.Generate($"ows-{server.Name}");
            var newLine = ServerAccessWriter.AutomationLine(pair.PublicKeyOpenSsh);
            var (desired, _) = await writer.DesiredAsync(server, ct);

            await RunRecordingFailureAsync(server, () => writer.WriteAsync(server, oldTarget, desired with { Wanted = [newLine, .. desired.Wanted] }, ct));
            var newTarget = writer.TargetFor(server, pair.PrivateKeyPem);
            if (!await CanSignInAsync(newTarget, ct))
            {
                await TryAsync(() => writer.WriteAsync(server, oldTarget, desired, ct));
                throw new RemoteAccessException("ows could not sign in with the new key, so it keeps using the current one. Nothing changed.");
            }

            // From here on the new key works. Store it before the old one is removed, so ows can never lose access.
            server.ReplaceAutomationKey(secrets.Protect(pair.PrivateKeyPem, server.TenantId, SecretPurposes.SshPrivateKey), newLine, clock.UtcNow);
            audit.Add(new AuditEntry(SshAuditTypes.AutomationKeyRotated,
                $"Rotated the ows key of {server.Name}; new key {SshPublicKey.Parse(newLine).Fingerprint}", access.UserId, tenantId));
            await unitOfWork.SaveChangesAsync(ct);

            await ApplyLockedAsync(server, extraRemove: [], ct, throwOnFailure: false);
            await DeleteProviderKeyAsync(server, ct);
            await unitOfWork.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Removes the ows key from the server and forgets it. Afterwards ows cannot install apps or change keys there
    /// until someone gives it access again.
    /// </summary>
    public async Task RevokeAutomationKeyAsync(Guid serverId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var server = await FindAsync(serverId, ct);
        RequireManageable(server);

        using (await locks.AcquireAsync(serverId, ct))
        {
            var (desired, serverGrants) = await writer.DesiredAsync(server, ct);
            if (!serverGrants.Any(g => g.State == GrantState.Present && !g.IsPending))
                throw new DomainException("No other key can sign in to this server yet. Give one of your keys access first, or nobody could sign in afterwards.");

            var automationBlob = SshPublicKey.FindInAuthorizedKeysLine(server.AutomationPublicKey!)!.Blob;
            var withoutOws = new DesiredKeys(desired.Wanted.Skip(1).ToList(), [.. desired.Remove, automationBlob]);
            var rewrite = await RunRecordingFailureAsync(server, () => writer.WriteAsync(server, writer.TargetFor(server), withoutOws, ct));

            var fingerprint = SshPublicKey.FindInAuthorizedKeysLine(server.AutomationPublicKey!)!.Fingerprint;
            server.RevokeAutomationKey();
            writer.Settle(server, serverGrants, rewrite, retiredKeyRemoved: true);
            await DeleteProviderKeyAsync(server, ct);
            audit.Add(new AuditEntry(SshAuditTypes.AutomationKeyRevoked,
                $"Revoked the ows key {fingerprint} of {server.Name}; ows no longer has access", access.UserId, tenantId));
            await unitOfWork.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Creates a new ows key for a server ows has no access to. ows can sign in once someone adds the returned
    /// public key to the server's authorized_keys.
    /// </summary>
    public async Task<string> RestoreAutomationKeyAsync(Guid serverId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var server = await FindAsync(serverId, ct);
        if (server.HasAutomationKey)
            throw new DomainException("ows already has a key for this server. Rotate it instead.");

        var pair = keyGenerator.Generate($"ows-{server.Name}");
        var line = ServerAccessWriter.AutomationLine(pair.PublicKeyOpenSsh);
        server.ReplaceAutomationKey(secrets.Protect(pair.PrivateKeyPem, server.TenantId, SecretPurposes.SshPrivateKey), line, clock.UtcNow);
        server.ResetAccessCheck(); // the background check tries the new key until someone has added it
        audit.Add(new AuditEntry(SshAuditTypes.AutomationKeyRestored,
            $"Created a new ows key {SshPublicKey.Parse(line).Fingerprint} for {server.Name}", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
        return line;
    }

    /// <summary>Accepts the next host key the server presents, e.g. after it was reinstalled.</summary>
    public async Task ForgetHostKeyAsync(Guid serverId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var server = await FindAsync(serverId, ct);
        var previous = server.HostKeyFingerprint ?? "none";
        server.ForgetHostKey();
        audit.Add(new AuditEntry(SshAuditTypes.HostKeyForgotten,
            $"Forgot the pinned host key {previous} of {server.Name}", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>Applies waiting changes; used by the background check. Failures are recorded on the server.</summary>
    public async Task ApplyInBackgroundAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct);
        if (server is not { CanManageKeys: true })
            return;
        await ApplyAsync(server, extraRemove: [], ct, throwOnFailure: false);
    }

    private async Task ApplyNowAsync(Server server, CancellationToken ct)
    {
        // Not reachable yet (e.g. still provisioning): the background check writes the change later.
        if (server.CanManageKeys)
            await ApplyAsync(server, extraRemove: [], ct);
    }

    private async Task ApplyAsync(Server server, IReadOnlyCollection<string> extraRemove, CancellationToken ct, bool throwOnFailure = true)
    {
        using (await locks.AcquireAsync(server.Id, ct))
            await ApplyLockedAsync(server, extraRemove, ct, throwOnFailure);
    }

    private async Task ApplyLockedAsync(Server server, IReadOnlyCollection<string> extraRemove, CancellationToken ct, bool throwOnFailure)
    {
        var (desired, serverGrants) = await writer.DesiredAsync(server, ct);
        var retired = server.RetiredAutomationPublicKey is not null;
        try
        {
            var rewrite = await writer.WriteAsync(server, writer.TargetFor(server), desired with { Remove = [.. desired.Remove, .. extraRemove] }, ct);
            writer.Settle(server, serverGrants, rewrite, retired);
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            server.RecordAccessFailure(clock.UtcNow, ex.Message);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            if (throwOnFailure)
                throw;
            return;
        }
        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task<T> RunRecordingFailureAsync<T>(Server server, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            server.RecordAccessFailure(clock.UtcNow, ex.Message);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> CanSignInAsync(RemoteTarget target, CancellationToken ct)
    {
        try
        {
            return (await shell.RunAsync(target, "true", ct)).Succeeded;
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            return false;
        }
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (IsAccessFailure(ex)) { /* best effort; the next check reports the state */ }
    }

    /// <summary>The first ows key was also registered at the provider; that copy is useless once the key is replaced.</summary>
    private async Task DeleteProviderKeyAsync(Server server, CancellationToken ct)
    {
        if (server.ExternalSshKeyId is not { } keyId)
            return;
        try
        {
            var provider = (await connections.ConnectAsync(server.GroupId, ct)).RequireServers();
            await provider.DeleteSshKeyAsync(keyId, ct);
            server.ForgetProviderSshKey();
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or ProviderNotConfiguredException or CapabilityNotSupportedException)
        {
            // Harmless: a provider key only matters when a server is created with it. A later rotation retries.
        }
    }

    private static bool IsAccessFailure(Exception ex) =>
        ex is RemoteAccessException or HostKeyMismatchException or TimeoutException or DomainException;

    private async Task<Server> FindAsync(Guid serverId, CancellationToken ct) =>
        await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");

    private static void RequireManageable(Server server)
    {
        if (!server.HasAutomationKey)
            throw new DomainException("ows has no access to this server.");
        if (!server.IsReachable)
            throw new DomainException("The server must be running with a public IPv4 address.");
    }

    private static SshPublicKey FindUnknown(Server server, string fingerprint) =>
        server.UnknownAuthorizedKeys.Select(SshPublicKey.FindInAuthorizedKeysLine).OfType<SshPublicKey>()
            .FirstOrDefault(k => k.Fingerprint == fingerprint)
        ?? throw new NotFoundException("Unknown key");

    private bool CanUse(SshKey key, Guid userId) =>
        access.Can(TenantRole.Admin) || (access.Can(TenantRole.Member) && (key.IsShared || key.OwnerUserId == userId));

    private static string OwnerName(SshKey key, IReadOnlyDictionary<Guid, string> names) =>
        key.OwnerUserId is { } owner ? names.GetValueOrDefault(owner, "former member") : "Shared";

    private static string CommentSuffix(SshPublicKey key) => key.Comment.Length > 0 ? $" ({key.Comment})" : "";
}
