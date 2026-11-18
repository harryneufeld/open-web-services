using System.Collections.Concurrent;
using System.Text;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.SshKeys;

namespace OpenWebServices.Application.Access;

/// <summary>One change of a server's authorized_keys at a time, whether it comes from a user or the background check.</summary>
public sealed class ServerAccessLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid serverId, CancellationToken ct)
    {
        var semaphore = _locks.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}

/// <summary>What a server's authorized_keys should contain.</summary>
/// <param name="Wanted">authorized_keys lines that must be present: the ows key first, then granted keys.</param>
/// <param name="Remove">Key blobs that must not be on the server.</param>
public sealed record DesiredKeys(IReadOnlyList<string> Wanted, IReadOnlyCollection<string> Remove);

/// <summary>
/// Reads and writes a server's authorized_keys over SSH with the ows automation key. The ows key is always part of
/// the wanted set unless a caller removes it on purpose, so a rewrite can never lock ows out by accident.
/// </summary>
public sealed class ServerAccessWriter(
    IServerKeyGrantRepository grants,
    ISshKeyRepository keys,
    IRemoteShell shell,
    ISecretProtector secrets,
    IClock clock)
{
    public const string AutomationLabel = "ows automation";
    private const string AuthorizedKeysPath = "~/.ssh/authorized_keys";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public RemoteTarget TargetFor(Server server, string? privateKeyPem = null)
    {
        if (server.PublicIpv4 is not { } host)
            throw new DomainException("The server has no public IPv4 address yet.");
        var key = privateKeyPem ?? (server.EncryptedSshPrivateKey is { } encrypted
            ? secrets.Unprotect(encrypted, server.TenantId, SecretPurposes.SshPrivateKey)
            : throw new DomainException("ows has no access to this server."));
        return new RemoteTarget(host, key, PinnedHostKey: server.HostKeyFingerprint);
    }

    /// <summary>The state the grants of a server ask for, plus everything revoked anywhere in its tenant.</summary>
    public async Task<(DesiredKeys Desired, IReadOnlyList<ServerKeyGrant> Grants)> DesiredAsync(Server server, CancellationToken ct)
    {
        var serverGrants = await grants.ListForServerAsync(server.Id, ct);
        // Background checks run across tenants; only this server's tenant counts.
        var allKeys = (await keys.ListAsync(ct)).Where(k => k.TenantId == server.TenantId).ToDictionary(k => k.Id);
        var activeBlobs = allKeys.Values.Where(k => !k.IsRevoked).Select(k => k.Key.Blob).ToHashSet(StringComparer.Ordinal);

        var wanted = new List<string>();
        if (server.AutomationPublicKey is { } automation)
            wanted.Add(AutomationLine(automation));

        var remove = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in serverGrants)
        {
            if (!allKeys.TryGetValue(grant.SshKeyId, out var key))
                continue;
            if (grant.State == GrantState.Present && !key.IsRevoked)
                wanted.Add(key.ToAuthorizedKeysLine());
            else
                remove.Add(key.Key.Blob);
        }

        // Revoked keys go from every server, also where ows never put them, unless someone added the key again.
        foreach (var revoked in allKeys.Values.Where(k => k.IsRevoked && !activeBlobs.Contains(k.Key.Blob)))
            remove.Add(revoked.Key.Blob);
        if (server.RetiredAutomationPublicKey is { } retired && SshPublicKey.FindInAuthorizedKeysLine(retired) is { } retiredKey)
            remove.Add(retiredKey.Blob);

        return (new DesiredKeys(wanted, remove), serverGrants);
    }

    /// <summary>Connects (pinning the host key on first contact), reads, rewrites if needed and returns the result.</summary>
    /// <exception cref="RemoteAccessException">ows could not connect or sign in, or the write failed.</exception>
    /// <exception cref="HostKeyMismatchException">The server presented another host key than the pinned one.</exception>
    public async Task<AuthorizedKeysRewrite> WriteAsync(Server server, RemoteTarget target, DesiredKeys desired, CancellationToken ct)
    {
        var hostKey = await shell.WaitUntilReadyAsync(target, ConnectTimeout, ct);
        if (server.HostKeyFingerprint is null)
            server.PinHostKey(hostKey.Algorithm, hostKey.Fingerprint);
        target = target with { PinnedHostKey = server.HostKeyFingerprint };

        var read = await shell.RunAsync(target, $"cat {AuthorizedKeysPath} 2>/dev/null || true", ct);
        if (!read.Succeeded)
            throw new RemoteAccessException($"Could not read {AuthorizedKeysPath} (exit code {read.ExitCode}).");

        var rewrite = AuthorizedKeysFile.Rewrite(read.Output, desired.Wanted, desired.Remove);
        if (!rewrite.Changed)
            return rewrite;

        // Written to a temporary file and moved into place, so sshd never sees a half-written file.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(rewrite.Content));
        var write = await shell.RunAsync(target,
            "umask 077 && mkdir -p ~/.ssh && " +
            $"printf '%s' '{encoded}' | base64 -d > {AuthorizedKeysPath}.ows-new && " +
            $"mv -f {AuthorizedKeysPath}.ows-new {AuthorizedKeysPath}", ct);
        if (!write.Succeeded)
            throw new RemoteAccessException($"Could not write {AuthorizedKeysPath}: {write.Output.Trim()}");
        return rewrite;
    }

    /// <summary>Records a successful write: grants are done, the retired ows key is gone, unknown keys are listed.</summary>
    public void Settle(Server server, IReadOnlyList<ServerKeyGrant> serverGrants, AuthorizedKeysRewrite rewrite, bool retiredKeyRemoved)
    {
        foreach (var grant in serverGrants)
        {
            if (grant.State == GrantState.Present)
                grant.MarkApplied(clock.UtcNow);
            else
                grants.Remove(grant);
        }
        if (retiredKeyRemoved)
            server.ClearRetiredAutomationKey();
        server.RecordAccessCheck(clock.UtcNow, rewrite.UnknownLines);
    }

    public static string AutomationLine(string automationPublicKey) =>
        SshPublicKey.FindInAuthorizedKeysLine(automationPublicKey)?.ToLine(AutomationLabel)
        ?? throw new DomainException("The stored ows automation key is not a valid public key.");
}
