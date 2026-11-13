namespace OpenWebServices.Domain.Providers;

/// <summary>
/// The kinds of resources a provider group can manage. A provider type declares which kinds it supports;
/// each group then chooses which of those to sync. New kinds (storage, registered domains, ...) are added here.
/// </summary>
[Flags]
public enum ResourceKind
{
    None = 0,
    Servers = 1,
    Dns = 2,
}

public static class ResourceKinds
{
    public static IReadOnlyList<ResourceKind> All { get; } = [ResourceKind.Servers, ResourceKind.Dns];

    public static string Label(ResourceKind kind) => kind switch
    {
        ResourceKind.Servers => "Servers",
        ResourceKind.Dns => "DNS zones",
        _ => kind.ToString(),
    };

    public static IEnumerable<ResourceKind> Split(ResourceKind kinds) => All.Where(k => kinds.HasFlag(k));
}
