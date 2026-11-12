using System.Text;

namespace OpenWebServices.Domain.Apps;

public enum AppInstallStatus
{
    Pending = 0,
    Installing = 1,
    Installed = 2,
    Failed = 3,
}

/// <summary>A request to install an app template on a server, and its progress.</summary>
public sealed class AppInstall
{
    private readonly StringBuilder _log = new();

    private AppInstall() { } // EF Core

    public Guid Id { get; private set; }
    public Guid ServerId { get; private set; }
    public string TemplateId { get; private set; } = "";
    public AppInstallStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }

    public string Log
    {
        get => _log.ToString();
        private set
        {
            _log.Clear();
            _log.Append(value);
        }
    }

    public static AppInstall Request(Guid serverId, string templateId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            throw new DomainException("A template is required.");

        return new AppInstall
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            TemplateId = templateId,
            Status = AppInstallStatus.Pending,
            CreatedAtUtc = nowUtc,
        };
    }

    public void Start()
    {
        if (Status != AppInstallStatus.Pending)
            throw new DomainException($"Cannot start an installation that is {Status}.");
        Status = AppInstallStatus.Installing;
    }

    public void AppendLog(string line) => _log.AppendLine(line);

    public void Complete(DateTime nowUtc) => Finish(AppInstallStatus.Installed, nowUtc);

    public void Fail(string reason, DateTime nowUtc)
    {
        AppendLog($"FAILED: {reason}");
        Finish(AppInstallStatus.Failed, nowUtc);
    }

    private void Finish(AppInstallStatus status, DateTime nowUtc)
    {
        if (Status != AppInstallStatus.Installing)
            throw new DomainException($"Cannot finish an installation that is {Status}.");
        Status = status;
        FinishedAtUtc = nowUtc;
    }
}
