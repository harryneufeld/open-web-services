using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Security;

/// <summary>Generates RSA key pairs: a PKCS#1 PEM private key and an OpenSSH public key line.</summary>
internal sealed class RsaSshKeyGenerator : ISshKeyGenerator
{
    private const int KeySizeBits = 3072;

    public SshKeyPair Generate(string comment)
    {
        using var rsa = RSA.Create(KeySizeBits);
        return new SshKeyPair(rsa.ExportRSAPrivateKeyPem(), PublicKeyLine(rsa, comment));
    }

    public string PublicKeyOf(string privateKeyPem, string comment)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return PublicKeyLine(rsa, comment);
    }

    private static string PublicKeyLine(RSA rsa, string comment)
    {
        var parameters = rsa.ExportParameters(includePrivateParameters: false);

        using var blob = new MemoryStream();
        WriteString(blob, "ssh-rsa"u8);
        WriteMultiPrecisionInteger(blob, parameters.Exponent!);
        WriteMultiPrecisionInteger(blob, parameters.Modulus!);

        return $"ssh-rsa {Convert.ToBase64String(blob.ToArray())} {comment}";
    }

    private static void WriteString(Stream stream, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
    }

    /// <summary>SSH "mpint": big-endian, no leading zeros, prefixed with 0x00 if the top bit is set.</summary>
    private static void WriteMultiPrecisionInteger(Stream stream, byte[] unsignedBigEndian)
    {
        var trimmed = unsignedBigEndian.AsSpan().TrimStart((byte)0);
        if (trimmed.Length > 0 && (trimmed[0] & 0x80) != 0)
        {
            var padded = new byte[trimmed.Length + 1];
            trimmed.CopyTo(padded.AsSpan(1));
            WriteString(stream, padded);
        }
        else
        {
            WriteString(stream, trimmed);
        }
    }
}
