using System.Globalization;

namespace OpenWebServices.Web.Services;

public static class Formatting
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Money(decimal amount, string currency) =>
        string.Create(German, $"{amount:N2} {(currency == "EUR" ? "€" : currency)}");

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
