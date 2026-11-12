namespace OpenWebServices.Domain.Servers;

public enum ServerStatus
{
    Provisioning = 0,
    Running = 1,
    Stopped = 2,
    Failed = 3,
    Deleting = 4,
}
