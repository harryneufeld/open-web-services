using System.Text;
using OpenWebServices.Application.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OpenWebServices.Infrastructure.Ssh;

/// <summary>
/// SSH.NET based shell. Host keys are accepted on first contact: the target is a server we just created
/// and its key is not known in advance.
/// </summary>
internal sealed class SshNetRemoteShell : IRemoteShell
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task WaitUntilReadyAsync(RemoteTarget target, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(() =>
                {
                    using var client = CreateClient(target);
                    client.Connect();
                }, ct);
                return;
            }
            catch (Exception ex) when (ex is SshException or IOException or System.Net.Sockets.SocketException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"SSH on {target.Host} did not become ready in time: {ex.Message}", ex);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    public Task<CommandResult> RunAsync(RemoteTarget target, string command, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var client = CreateClient(target);
            client.Connect();
            using var cmd = client.CreateCommand(command);
            cmd.Execute();
            return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result + cmd.Error);
        }, ct);

    public Task UploadTextAsync(RemoteTarget target, string remotePath, string content, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var client = new SftpClient(CreateConnectionInfo(target));
            client.Connect();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            client.UploadFile(stream, remotePath, canOverride: true);
        }, ct);

    private static SshClient CreateClient(RemoteTarget target) => new(CreateConnectionInfo(target));

    private static PrivateKeyConnectionInfo CreateConnectionInfo(RemoteTarget target)
    {
        using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(target.PrivateKeyPem));
        var key = new PrivateKeyFile(keyStream);
        return new PrivateKeyConnectionInfo(target.Host, 22, target.User, key) { Timeout = ConnectTimeout };
    }
}
