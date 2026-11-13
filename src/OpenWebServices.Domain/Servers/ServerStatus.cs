namespace OpenWebServices.Domain.Servers;

public enum ServerStatus
{
    Provisioning = 0,
    Running = 1,
    Stopped = 2,
    Failed = 3,
    Deleting = 4,
    Starting = 5,
    Stopping = 6,
}

/// <summary>Where a server record came from.</summary>
public enum ServerOrigin
{
    /// <summary>Created through this platform; it holds the server's SSH key.</summary>
    Created = 0,

    /// <summary>Found at the provider during a sync; no SSH key is available.</summary>
    Imported = 1,
}

/// <summary>The power state a provider reports for a server, normalized across providers.</summary>
public enum ObservedServerState
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
}
