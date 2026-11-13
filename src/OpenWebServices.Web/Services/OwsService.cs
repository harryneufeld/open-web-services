namespace OpenWebServices.Web.Services;

/// <summary>
/// ows's tongue-in-cheek service names. Every parody name is always shown together with its plain label,
/// so the joke never costs anyone the meaning.
/// </summary>
public sealed record OwsService(string Key, string Name, string Badge, string Plain, string Tagline, string? Href)
{
    public static readonly OwsService Servers = new("oc2", "OC2", "OC2", "Servers",
        "Ordinary Compute Cloud. Servers at any provider you connect.", "/servers");

    public static readonly OwsService Dns = new("r66", "Route 66", "66", "DNS",
        "DNS that takes you where you're going. Get your kicks on port 53.", "/dns");

    public static readonly OwsService Groups = new("iou", "IOU", "IOU", "Provider groups",
        "Identity of Owners & Users. Who's paying for what, honestly.", "/groups");

    public static readonly OwsService Storage = new("s4", "S4", "S4", "Storage",
        "Simply Some Storage Service. One more S than you'd expect.", null);
}
