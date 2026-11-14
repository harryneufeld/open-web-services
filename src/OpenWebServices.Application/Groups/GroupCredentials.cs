using System.Text.Json;
using OpenWebServices.Application.Abstractions;

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

    /// <summary>
    /// What the UI may show of stored credentials: plain values for non-secret fields, and for secrets only a mask
    /// with the last four characters of long values, so an admin can tell which key is in use.
    /// </summary>
    public static IReadOnlyList<MaskedCredential> Mask(ProviderTypeInfo type, IReadOnlyDictionary<string, string> credentials) =>
        [.. type.CredentialFields
            .Where(field => !string.IsNullOrEmpty(credentials.GetValueOrDefault(field.Key)))
            .Select(field => new MaskedCredential(field.Key, field.Label,
                field.IsSecret ? MaskSecret(credentials[field.Key]) : credentials[field.Key]))];

    private const int MinLengthToRevealTail = 16;
    private const int RevealedTail = 4;
    private const string Dots = "••••••••";

    private static string MaskSecret(string value) =>
        value.Length >= MinLengthToRevealTail ? Dots + value[^RevealedTail..] : Dots;
}

/// <summary>A stored credential value as it may be displayed; secrets are masked.</summary>
public sealed record MaskedCredential(string Key, string Label, string Display);
