using System.Net.Sockets;
using System.Text;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.SshKeys;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OpenWebServices.Infrastructure.Ssh;

/// <summary>
/// SSH.NET based shell. The host key is pinned per server: the first connection to a new server accepts and reports
/// it, every later connection must present the same key or is refused before anything is sent.
/// </summary>
internal sealed class SshNetRemoteShell : IRemoteShell
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<HostKey> WaitUntilReadyAsync(RemoteTarget target, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await Task.Run(() =>
                {
                    using var connection = Connect(new SshClient(CreateConnectionInfo(target)), target);
                    return connection.HostKey;
                }, ct);
            }
            catch (RemoteAccessException ex) when (ex.InnerException is not SshAuthenticationException)
            {
                // Not reachable yet (booting, sshd starting). A rejected key or a wrong host key will not improve.
                if (DateTime.UtcNow >= deadline)
                    throw new RemoteAccessException($"SSH on {target.Host} did not become ready in time: {ex.Message}", ex);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    public Task<CommandResult> RunAsync(RemoteTarget target, string command, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var connection = Connect(new SshClient(CreateConnectionInfo(target)), target);
            using var cmd = connection.Client.CreateCommand(command);
            cmd.Execute();
            return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result + cmd.Error);
        }, ct);

    public Task UploadTextAsync(RemoteTarget target, string remotePath, string content, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var connection = Connect(new SftpClient(CreateConnectionInfo(target)), target);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            connection.Client.UploadFile(stream, remotePath, canOverride: true);
        }, ct);

    /// <summary>Connects, checking the host key against the pin, and turns SSH.NET errors into ows errors.</summary>
    private static Connection<T> Connect<T>(T client, RemoteTarget target) where T : BaseClient
    {
        HostKey? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = new HostKey(Algorithm(e.HostKeyName), SshPublicKey.FingerprintOf(e.HostKey));
            e.CanTrust = target.PinnedHostKey is null || target.PinnedHostKey == seen.Fingerprint;
        };

        try
        {
            client.Connect();
            return new Connection<T>(client, seen!);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException)
        {
            client.Dispose();
            if (seen is not null && target.PinnedHostKey is { } pinned && pinned != seen.Fingerprint)
                throw new HostKeyMismatchException(target.Host, pinned, $"{seen.Algorithm} {seen.Fingerprint}");
            throw ex is SshAuthenticationException
                ? new RemoteAccessException($"{target.Host} refused the ows key. It may have been removed from the server's authorized_keys.", ex)
                : new RemoteAccessException($"Could not connect to {target.Host} on port 22: {ex.Message}", ex);
        }
    }

    private static string Algorithm(string hostKeyName) => hostKeyName switch
    {
        "ssh-ed25519" => "ED25519",
        _ when hostKeyName.StartsWith("ecdsa-", StringComparison.Ordinal) => "ECDSA",
        _ when hostKeyName.StartsWith("rsa-", StringComparison.Ordinal) || hostKeyName == "ssh-rsa" => "RSA",
        _ => hostKeyName.ToUpperInvariant(),
    };

    private static PrivateKeyConnectionInfo CreateConnectionInfo(RemoteTarget target)
    {
        using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(target.PrivateKeyPem));
        var key = new PrivateKeyFile(keyStream);
        return new PrivateKeyConnectionInfo(target.Host, 22, target.User, key) { Timeout = ConnectTimeout };
    }

    private sealed class Connection<T>(T client, HostKey hostKey) : IDisposable where T : BaseClient
    {
        public T Client { get; } = client;
        public HostKey HostKey { get; } = hostKey;
        public void Dispose() => Client.Dispose();
    }
}
