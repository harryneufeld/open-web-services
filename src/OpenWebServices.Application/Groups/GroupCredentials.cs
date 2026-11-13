using System.Text.Json;

namespace OpenWebServices.Application.Groups;

/// <summary>
/// Converts a group's credential fields to and from the plain text that is encrypted at rest.
/// Each provider type defines its own fields (see <see cref="Abstractions.ProviderTypeInfo.CredentialFields"/>).
/// </summary>
public static class GroupCredentials
{
    /// <summary>Key used by single-token providers; also what credentials stored before groups existed map to.</summary>
    public const string ApiTokenKey = "api_token";

    public static string Serialize(IReadOnlyDictionary<string, string> credentials) =>
        JsonSerializer.Serialize(credentials);

    public static IReadOnlyDictionary<string, string> Deserialize(string plainText)
    {
        if (!plainText.TrimStart().StartsWith('{'))
            return new Dictionary<string, string> { [ApiTokenKey] = plainText }; // stored before groups existed

        return JsonSerializer.Deserialize<Dictionary<string, string>>(plainText) ?? [];
    }
}
