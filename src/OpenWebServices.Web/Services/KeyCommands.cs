namespace OpenWebServices.Web.Services;

public enum ClientOs { Unix, Windows }

/// <summary>Shell commands shown after a key pair was generated in the browser, per operating system.</summary>
public static class KeyCommands
{
    public static IReadOnlyList<ClientOs> All { get; } = [ClientOs.Unix, ClientOs.Windows];

    public static string Label(ClientOs os) => os == ClientOs.Windows ? "Windows (PowerShell)" : "macOS / Linux";

    /// <param name="Install">Moves the downloaded key into the SSH folder, restricts it to the user, adds a passphrase.</param>
    /// <param name="Test">Signs in with this key only, so other keys of ssh-agent or ~/.ssh cannot hide a missing grant.</param>
    public sealed record Commands(string Install, string Test);

    public static Commands For(ClientOs os, string fileName) => os == ClientOs.Windows
        ? new Commands(
            "New-Item -ItemType Directory -Force \"$HOME\\.ssh\" | Out-Null; " +
            $"Move-Item \"$HOME\\Downloads\\{fileName}\" \"$HOME\\.ssh\\{fileName}\"; " +
            $"icacls \"$HOME\\.ssh\\{fileName}\" /inheritance:r /grant:r \"${{env:USERNAME}}:F\"; " +
            $"ssh-keygen -p -f \"$HOME\\.ssh\\{fileName}\"",
            $"ssh -o IdentitiesOnly=yes -i \"$HOME\\.ssh\\{fileName}\" root@SERVER-IP")
        : new Commands(
            $"mkdir -p ~/.ssh && mv ~/Downloads/{fileName} ~/.ssh/ && chmod 600 ~/.ssh/{fileName} && ssh-keygen -p -f ~/.ssh/{fileName}",
            $"ssh -o IdentitiesOnly=yes -i ~/.ssh/{fileName} root@SERVER-IP");
}
