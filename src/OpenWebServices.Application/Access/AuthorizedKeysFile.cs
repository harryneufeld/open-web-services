using OpenWebServices.Domain.SshKeys;

namespace OpenWebServices.Application.Access;

/// <summary>The rewritten file and the keys in it that ows does not know.</summary>
/// <param name="Content">The new authorized_keys content.</param>
/// <param name="Changed">The content differs from the original, so it must be written.</param>
/// <param name="UnknownLines">Key lines outside the ows block whose key ows neither manages nor removes.</param>
public sealed record AuthorizedKeysRewrite(string Content, bool Changed, IReadOnlyList<string> UnknownLines);

/// <summary>
/// Rewrites a server's authorized_keys. ows owns one marked block and writes exactly the wanted keys into it.
/// Lines outside the block are kept as they are, except copies of keys ows manages (they move into the block) and
/// keys that must go (revoked keys, a retired ows key, an unknown key a user removed).
/// </summary>
public static class AuthorizedKeysFile
{
    public const string BeginMarker = "# >>> ows managed keys: changes inside this block are overwritten >>>";
    public const string EndMarker = "# <<< ows managed keys <<<";

    /// <param name="content">The current file; empty when it does not exist.</param>
    /// <param name="wanted">authorized_keys lines that must be present, in order.</param>
    /// <param name="removeBlobs">Key blobs that must not be in the file anywhere.</param>
    public static AuthorizedKeysRewrite Rewrite(string content, IReadOnlyList<string> wanted, IReadOnlyCollection<string> removeBlobs)
    {
        var wantedKeys = wanted.Select(line => SshPublicKey.FindInAuthorizedKeysLine(line)
                                               ?? throw new ArgumentException($"Not a key line: {line}", nameof(wanted)))
            .ToList();
        var managedBlobs = wantedKeys.Select(k => k.Blob).ToHashSet(StringComparer.Ordinal);
        var remove = removeBlobs.ToHashSet(StringComparer.Ordinal);

        var outside = new List<string>();
        var unknown = new List<string>();
        var inBlock = false;
        foreach (var raw in content.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line == BeginMarker)
            {
                inBlock = true;
                continue;
            }
            if (line == EndMarker)
            {
                inBlock = false;
                continue;
            }
            if (inBlock)
                continue; // ows rewrites its own block completely

            var key = SshPublicKey.FindInAuthorizedKeysLine(line);
            if (key is not null && (managedBlobs.Contains(key.Blob) || remove.Contains(key.Blob)))
                continue;
            if (key is not null)
                unknown.Add(line);
            outside.Add(line);
        }

        while (outside.Count > 0 && outside[^1].Length == 0)
            outside.RemoveAt(outside.Count - 1);

        var lines = new List<string>(outside);
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(BeginMarker);
        lines.AddRange(wanted.DistinctBy(line => SshPublicKey.FindInAuthorizedKeysLine(line)!.Blob, StringComparer.Ordinal));
        lines.Add(EndMarker);
        var result = string.Join('\n', lines) + "\n";

        return new AuthorizedKeysRewrite(result, result != content, unknown);
    }
}
