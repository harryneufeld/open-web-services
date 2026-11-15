using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Application.Offers;

/// <summary>
/// One line in the offer list: the same size, location, billing and price offered by one or more groups. Groups of the
/// same provider usually sell identical offers, so they share a line and the group is chosen when ordering.
/// </summary>
/// <param name="Offers">The offer as each group sells it, in the order they were listed.</param>
public sealed record OfferRow(IReadOnlyList<ServerOffer> Offers)
{
    /// <summary>The offer shown for the line: the first group that can deliver it now, else the first group.</summary>
    public ServerOffer Offer => Offers.FirstOrDefault(o => o.Available) ?? Offers[0];

    public bool Available => Offers.Any(o => o.Available);

    /// <summary>Groups that can deliver the offer now; the order form picks one of them.</summary>
    public IReadOnlyList<ServerOffer> Orderable => [.. Offers.Where(o => o.Available)];

    public IReadOnlyList<string> GroupNames => [.. Offers.Select(o => o.GroupName).Distinct()];

    public string Key => Offers[0].Key;

    /// <summary>Merges offers that differ only in their group, keeping the order of their first appearance.</summary>
    public static IReadOnlyList<OfferRow> Merge(IEnumerable<ServerOffer> offers) =>
        [.. offers
            .GroupBy(o => (o.ProviderType, o.Location, o.TypeName, BillingId: o.Billing?.Id, o.MonthlyNet, o.MonthlyGross,
                o.Currency, o.VCpus, o.MemoryGb, o.DiskGb))
            .Select(g => new OfferRow([.. g]))];
}
