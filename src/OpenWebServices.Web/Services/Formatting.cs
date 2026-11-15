using System.Globalization;

namespace OpenWebServices.Web.Services;

public static class Formatting
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Money(decimal amount, string currency) =>
        string.Create(German, $"{amount:N2} {(currency == "EUR" ? "€" : currency)}");

    /// <summary>
    /// The address to use from an IPv6 value: "2001:db8::/64" → "2001:db8::1" (the first address, as providers assign
    /// it), "2001:db8::7" stays as it is.
    /// </summary>
    public static string Ipv6Address(string value)
    {
        var address = value.Split('/')[0];
        return address.EndsWith(':') ? address + "1" : address;
    }

    /// <summary>99.9 → "99.9 %", 99.95 → "99.95 %".</summary>
    public static string Percent(decimal value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0.###} %");

    /// <summary>English name of an ISO country code ("DE" → "Germany"), or the code when it is unknown.</summary>
    public static string Country(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "Unknown";
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    /// <summary>"just now", "5 min ago", "3 h ago", or a date for anything older than a day.</summary>
    public static string Ago(DateTime? utc)
    {
        if (utc is not { } value)
            return "never";
        var age = DateTime.UtcNow - value;
        return age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
            : value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }
}
