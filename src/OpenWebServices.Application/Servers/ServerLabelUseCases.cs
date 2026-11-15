using System.Text.RegularExpressions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

/// <param name="Labels">Null when the provider has no labels or the server does not exist there yet.</param>
/// <param name="Unavailable">Why <paramref name="Labels"/> is null.</param>
public sealed record ServerLabelsView(IReadOnlyDictionary<string, string>? Labels, string? Unavailable);

/// <summary>Key/value labels of a server, read and written live at the provider.</summary>
public sealed partial class ServerLabels(AccessContext access, IProviderConnectionFactory connections, IServerRepository servers)
{
    public const int MaxLabels = 100;

    public async Task<ServerLabelsView> GetAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is not { } externalId)
            return new ServerLabelsView(null, "The server has not been created at the provider yet.");

        var connection = await connections.ConnectAsync(server.GroupId, ct);
        return connection.ServerLabels is { } labels
            ? new ServerLabelsView(await labels.GetLabelsAsync(externalId, ct), null)
            : new ServerLabelsView(null, $"{connection.Type.DisplayName} has no server labels.");
    }

    /// <summary>Replaces all labels of the server.</summary>
    public async Task SaveAsync(Guid serverId, IReadOnlyList<KeyValuePair<string, string>> labels, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var normalized = Normalize(labels);
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (server.ExternalId is null)
            throw new DomainException("The server has not been created at the provider yet.");
        var connection = await connections.ConnectAsync(server.GroupId, ct);
        var provider = connection.ServerLabels ?? throw new FeatureNotSupportedException(connection.Type.DisplayName, "server labels");
        await provider.SetLabelsAsync(server.ExternalId, normalized, ct);
    }

    /// <summary>
    /// Keys: an optional DNS-style prefix and "/", then a name of 1-63 characters; names and values start and end with a
    /// letter or digit and may contain '-', '_' and '.' (the label syntax Hetzner and Kubernetes share). Values may be empty.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Normalize(IReadOnlyList<KeyValuePair<string, string>> labels)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in labels)
        {
            var key = rawKey.Trim();
            var value = rawValue.Trim();
            if (key.Length == 0 && value.Length == 0)
                continue;
            if (!ValidKey().IsMatch(key))
                throw new DomainException($"'{rawKey}' is not a valid label key. Use letters, digits, '-', '_' and '.', optionally with a prefix such as example.com/.");
            if (value.Length > 0 && !ValidValue().IsMatch(value))
                throw new DomainException($"The value of '{key}' is not valid. Use up to 63 letters, digits, '-', '_' and '.', starting and ending with a letter or digit.");
            if (!result.TryAdd(key, value))
                throw new DomainException($"The label '{key}' appears twice.");
        }

        return result.Count > MaxLabels ? throw new DomainException($"A server can have at most {MaxLabels} labels.") : result;
    }

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*/)?[A-Za-z0-9]([A-Za-z0-9._-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex ValidKey();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex ValidValue();
}
