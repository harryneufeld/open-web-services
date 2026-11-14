namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>
/// Readable names for OVHcloud region codes. The API only exposes them through region details, which many consumer
/// keys are not granted, so they are derived from the code: "GRA11" → Gravelines, "EU-WEST-LZ-BRU-A" → Brussels.
/// Unknown codes fall back to the code itself.
/// </summary>
internal static class OvhRegions
{
    private static readonly Dictionary<string, (string City, string Country)> Sites = new(StringComparer.OrdinalIgnoreCase)
    {
        // Public Cloud regions
        ["GRA"] = ("Gravelines", "FR"),
        ["SBG"] = ("Strasbourg", "FR"),
        ["RBX"] = ("Roubaix", "FR"),
        ["PAR"] = ("Paris", "FR"),
        ["BHS"] = ("Beauharnois", "CA"),
        ["TOR"] = ("Toronto", "CA"),
        ["DE"] = ("Frankfurt", "DE"),
        ["LIM"] = ("Limburg", "DE"),
        ["UK"] = ("London", "GB"),
        ["WAW"] = ("Warsaw", "PL"),
        ["MIL"] = ("Milan", "IT"),
        ["SGP"] = ("Singapore", "SG"),
        ["SYD"] = ("Sydney", "AU"),
        ["MUM"] = ("Mumbai", "IN"),
        ["YNM"] = ("Mumbai", "IN"),
        ["VA"] = ("Vint Hill", "US"),
        ["OR"] = ("Hillsboro", "US"),
        // Local zones
        ["AMS"] = ("Amsterdam", "NL"),
        ["BRU"] = ("Brussels", "BE"),
        ["LUX"] = ("Luxembourg", "LU"),
        ["MAD"] = ("Madrid", "ES"),
        ["LIS"] = ("Lisbon", "PT"),
        ["MRS"] = ("Marseille", "FR"),
        ["ZRH"] = ("Zurich", "CH"),
        ["VIE"] = ("Vienna", "AT"),
        ["PRG"] = ("Prague", "CZ"),
        ["CPH"] = ("Copenhagen", "DK"),
        ["HEL"] = ("Helsinki", "FI"),
        ["OSL"] = ("Oslo", "NO"),
        ["STO"] = ("Stockholm", "SE"),
        ["NYC"] = ("New York", "US"),
        ["ATL"] = ("Atlanta", "US"),
        ["BOS"] = ("Boston", "US"),
        ["CHI"] = ("Chicago", "US"),
        ["DAL"] = ("Dallas", "US"),
        ["MIA"] = ("Miami", "US"),
        ["LAX"] = ("Los Angeles", "US"),
        ["SEA"] = ("Seattle", "US"),
        ["DEN"] = ("Denver", "US"),
        ["PHX"] = ("Phoenix", "US"),
    };

    /// <summary>City and ISO country code of a region, or the region code and "" when it is not known.</summary>
    public static (string City, string Country) Describe(string region)
    {
        // The most specific part comes last ("EU-WEST-LZ-BRU-A"), so parts are checked from the end; digits are
        // dropped so "GRA11" and "SBG5" match their site.
        foreach (var part in region.Split('-').Reverse())
        {
            var site = part.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (Sites.TryGetValue(site, out var known))
                return known;
        }

        return (region, "");
    }
}
