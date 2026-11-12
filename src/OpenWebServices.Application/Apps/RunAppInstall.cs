using System.Security.Cryptography;
using System.Text;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Apps;

/// <summary>Executes a pending app installation on its server via SSH and Docker Compose.</summary>
public sealed class RunAppInstall(
    IAppInstallRepository installs,
    IServerRepository servers,
    IAppTemplateCatalog templates,
    IRemoteShell shell,
    ISecretProtector secrets,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private static readonly TimeSpan SshReadyTimeout = TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(Guid installId, CancellationToken ct)
    {
        var install = await installs.FindAsync(installId, ct) ?? throw new NotFoundException("Installation");
        install.Start();
        await unitOfWork.SaveChangesAsync(ct);

        try
        {
            var server = await servers.FindAsync(install.ServerId, ct) ?? throw new NotFoundException("Server");
            var template = templates.Find(install.TemplateId) ?? throw new NotFoundException("App template");
            await InstallAsync(install, server, template, ct);
            install.Complete(clock.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            install.Fail(ex.Message, clock.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    private async Task InstallAsync(AppInstall install, Server server, AppTemplate template, CancellationToken ct)
    {
        var target = new RemoteTarget(server.PublicIpv4!, secrets.Unprotect(server.EncryptedSshPrivateKey));
        var directory = $"/opt/apps/{template.Id}";

        install.AppendLog("Waiting for SSH and server bootstrap (Docker)...");
        await shell.WaitUntilReadyAsync(target, SshReadyTimeout, ct);
        await RunStepAsync(install, target, "cloud-init status --wait", ct);

        install.AppendLog($"Uploading {template.Name} to {directory}");
        await RunStepAsync(install, target, $"mkdir -p {directory}", ct);
        await shell.UploadTextAsync(target, $"{directory}/docker-compose.yml", template.ComposeYaml, ct);
        await shell.UploadTextAsync(target, $"{directory}/.env", BuildEnvFile(template), ct);

        await RunStepAsync(install, target, $"cd {directory} && docker compose up -d", ct);
        if (template.GeneratedSecrets.Count > 0)
            install.AppendLog($"Generated secrets are stored in {directory}/.env on the server.");
    }

    private async Task RunStepAsync(AppInstall install, RemoteTarget target, string command, CancellationToken ct)
    {
        install.AppendLog($"$ {command}");
        var result = await shell.RunAsync(target, command, ct);
        if (!string.IsNullOrWhiteSpace(result.Output))
            install.AppendLog(result.Output.TrimEnd());
        if (!result.Succeeded)
            throw new InvalidOperationException($"'{command}' exited with code {result.ExitCode}.");
    }

    private static string BuildEnvFile(AppTemplate template)
    {
        var env = new StringBuilder();
        foreach (var name in template.GeneratedSecrets)
            env.AppendLine($"{name}={Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}");
        return env.ToString();
    }
}
