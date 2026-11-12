using System.Globalization;

namespace OpenWebServices.Web.Services;

public static class Formatting
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Money(decimal amount, string currency) =>
        string.Create(German, $"{amount:N2} {(currency == "EUR" ? "€" : currency)}");
}
