using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenWebServices.Domain.SshKeys;

/// <summary>
/// An OpenSSH public key ("type base64 [comment]"), checked for a known algorithm, a well-formed key blob and a safe
/// size. Two keys are the same key when their <see cref="Blob"/> matches; the comment is only a label.
/// </summary>
public sealed partial record SshPublicKey
{
    public const int MinRsaBits = 2048;
    public const int MaxCommentLength = 100;

    private static readonly Dictionary<string, string> Algorithms = new(StringComparer.Ordinal)
    {
        ["ssh-ed25519"] = "ED25519",
        ["sk-ssh-ed25519@openssh.com"] = "ED25519-SK",
        ["ecdsa-sha2-nistp256"] = "ECDSA",
        ["ecdsa-sha2-nistp384"] = "ECDSA",
        ["ecdsa-sha2-nistp521"] = "ECDSA",
        ["sk-ecdsa-sha2-nistp256@openssh.com"] = "ECDSA-SK",
        ["ssh-rsa"] = "RSA",
    };

    private SshPublicKey(string type, string blob, string comment, int bits)
    {
        Type = type;
        Blob = blob;
        Comment = comment;
        Bits = bits;
    }

    /// <summary>The key type as written in the key, e.g. "ssh-ed25519".</summary>
    public string Type { get; }

    /// <summary>The base64 key blob; identifies the key.</summary>
    public string Blob { get; }

    public string Comment { get; }

    /// <summary>Key size in bits, as ssh-keygen reports it.</summary>
    public int Bits { get; }

    /// <summary>Short algorithm name as ssh-keygen shows it, e.g. "ED25519".</summary>
    public string Algorithm => Algorithms[Type];

    /// <summary>SHA256 fingerprint as ssh-keygen -l prints it, e.g. "SHA256:uN0…".</summary>
    public string Fingerprint => FingerprintOf(Convert.FromBase64String(Blob));

    /// <summary>The key as one authorized_keys line, with the given comment instead of its own.</summary>
    public string ToLine(string? comment = null)
    {
        var label = Sanitize(comment ?? Comment);
        return label.Length == 0 ? $"{Type} {Blob}" : $"{Type} {Blob} {label}";
    }

    public override string ToString() => ToLine();

    /// <exception cref="DomainException">The text is not a single supported public key.</exception>
    public static SshPublicKey Parse(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            throw new DomainException("Paste a public key, e.g. the contents of ~/.ssh/id_ed25519.pub.");
        if (trimmed.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(
                "This is a private key. Never share it: paste the public key instead, the file ending in .pub.");
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
            throw new DomainException("Paste one key per entry, on a single line.");

        var parts = trimmed.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new DomainException("A public key has the form 'ssh-ed25519 AAAA… comment'.");
        return FromParts(parts[0], parts[1], parts.Length > 2 ? parts[2] : "");
    }

    public static bool TryParse(string text, out SshPublicKey? key)
    {
        try
        {
            key = Parse(text);
            return true;
        }
        catch (DomainException)
        {
            key = null;
            return false;
        }
    }

    /// <summary>
    /// Finds the key in one authorized_keys line, which may start with options such as
    /// <c>no-port-forwarding,command="…"</c>. Returns null for blank lines, comments and lines without a valid key.
    /// </summary>
    public static SshPublicKey? FindInAuthorizedKeysLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;
        foreach (Match match in KeyInLine().Matches(trimmed))
        {
            if (!Algorithms.ContainsKey(match.Groups["type"].Value))
                continue;
            try
            {
                return FromParts(match.Groups["type"].Value, match.Groups["blob"].Value, match.Groups["comment"].Value);
            }
            catch (DomainException)
            {
                // a token that only looks like a key, e.g. inside an option; keep looking
            }
        }
        return null;
    }

    /// <summary>"SHA256:" plus the unpadded base64 SHA-256 of a key blob, the format OpenSSH uses.</summary>
    public static string FingerprintOf(byte[] blob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    private static SshPublicKey FromParts(string type, string base64, string comment)
    {
        if (!Algorithms.ContainsKey(type))
            throw new DomainException(type == "ssh-dss"
                ? "DSA keys are no longer considered secure. Create an Ed25519 key: ssh-keygen -t ed25519"
                : $"Unsupported key type '{Truncate(type, 40)}'. Use Ed25519, ECDSA or RSA ({MinRsaBits} bits or more).");

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new DomainException("The key data is not valid base64. Copy the whole line from the .pub file.");
        }

        var reader = new BlobReader(blob);
        var embeddedType = reader.ReadString();
        if (embeddedType != type)
            throw new DomainException("The key data does not match its type. Copy the whole line from the .pub file.");

        var bits = type switch
        {
            "ssh-rsa" => RsaBits(reader),
            "ssh-ed25519" or "sk-ssh-ed25519@openssh.com" => Ed25519Bits(reader),
            _ => EcdsaBits(type),
        };
        if (type == "ssh-rsa" && bits < MinRsaBits)
            throw new DomainException($"This RSA key has {bits} bits; at least {MinRsaBits} are required. Better: ssh-keygen -t ed25519");

        return new SshPublicKey(type, Convert.ToBase64String(blob), Sanitize(comment), bits);
    }

    private static int RsaBits(BlobReader reader)
    {
        reader.ReadBytes(); // public exponent
        var modulus = reader.ReadBytes().AsSpan().TrimStart((byte)0);
        if (modulus.Length == 0)
            throw new DomainException("The RSA key is incomplete.");
        var topByteBits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)modulus[0]);
        return (modulus.Length - 1) * 8 + topByteBits;
    }

    private static int Ed25519Bits(BlobReader reader)
    {
        if (reader.ReadBytes().Length != 32)
            throw new DomainException("The Ed25519 key is incomplete.");
        return 256;
    }

    private static int EcdsaBits(string type) => type switch
    {
        "ecdsa-sha2-nistp384" => 384,
        "ecdsa-sha2-nistp521" => 521,
        _ => 256,
    };

    /// <summary>Comments end up in authorized_keys and in the UI: one line of printable text, length-limited.</summary>
    private static string Sanitize(string comment)
    {
        var builder = new StringBuilder();
        foreach (var c in comment.Trim())
            builder.Append(char.IsControl(c) ? ' ' : c);
        return Truncate(builder.ToString().Trim(), MaxCommentLength);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"(?:^|\s)(?<type>[a-z0-9@.-]+)\s+(?<blob>AAAA[A-Za-z0-9+/]+={0,3})(?:\s+(?<comment>.*))?$")]
    private static partial Regex KeyInLine();

    private ref struct BlobReader(byte[] data)
    {
        private int _offset;

        public byte[] ReadBytes()
        {
            if (_offset + 4 > data.Length)
                throw new DomainException("The key data is incomplete. Copy the whole line from the .pub file.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_offset, 4));
            _offset += 4;
            if (length > data.Length - _offset)
                throw new DomainException("The key data is incomplete. Copy the whole line from the .pub file.");
            var value = data.AsSpan(_offset, (int)length).ToArray();
            _offset += (int)length;
            return value;
        }

        public string ReadString() => Encoding.ASCII.GetString(ReadBytes());
    }
}
