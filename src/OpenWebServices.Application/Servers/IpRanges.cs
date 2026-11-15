using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenWebServices.Domain;

namespace OpenWebServices.Application.Servers;

/// <summary>Parsing and normalizing IP ranges in CIDR notation.</summary>
public static class IpRanges
{
    public const string AnyIpv4 = "0.0.0.0/0";
    public const string AnyIpv6 = "::/0";

    /// <summary>
    /// "203.0.113.7" → "203.0.113.7/32", "10.1.2.3/8" → "10.0.0.0/8". Providers reject ranges with host bits set.
    /// </summary>
    /// <exception cref="DomainException">Not an IP address or range.</exception>
    public static string Normalize(string value)
    {
        var (address, prefix) = Parse(value);
        return $"{Mask(address, prefix)}/{prefix.ToString(CultureInfo.InvariantCulture)}";
    }

    public static (IPAddress Address, int Prefix) Parse(string value)
    {
        var text = value.Trim();
        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressText, out var address) || addressText.Contains('%'))
            throw new DomainException($"'{value}' is not an IP address or range such as 203.0.113.0/24.");

        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > max))
            throw new DomainException($"'{value}' has an invalid prefix length; use 0-{max}.");
        return (address, prefix);
    }

    /// <summary>True for IPv4 ranges inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16.</summary>
    public static bool IsPrivateIpv4(IPAddress address, int prefix)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        return (b[0] == 10 && prefix >= 8)
               || (b[0] == 172 && (b[1] & 0xF0) == 16 && prefix >= 12)
               || (b[0] == 192 && b[1] == 168 && prefix >= 16);
    }

    /// <summary>
    /// The first /24 inside <paramref name="range"/> that overlaps none of <paramref name="taken"/>, or the whole range
    /// when it is smaller than a /24 and empty. Null when the range is full.
    /// </summary>
    public static string? NextFreeSubnet(string range, IReadOnlyCollection<string> taken)
    {
        var (address, prefix) = Parse(range);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var used = taken.Select(Parse).Select(t => (Start: ToUInt(Mask(t.Address, t.Prefix)), Size: Size(t.Prefix))).ToList();
        var start = ToUInt(Mask(address, prefix));
        if (prefix > 24)
            return used.Count == 0 ? Normalize(range) : null;

        var end = (ulong)start + Size(prefix);
        for (ulong candidate = start; candidate < end; candidate += 256)
        {
            if (!used.Any(u => candidate < u.Start + u.Size && u.Start < candidate + 256))
                return $"{FromUInt((uint)candidate)}/24";
        }

        return null;
    }

    private static ulong Size(int prefix) => 1UL << (32 - prefix);

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPAddress(bytes);
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
