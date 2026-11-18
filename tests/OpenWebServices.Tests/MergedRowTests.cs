using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Domains;
using OpenWebServices.Application.Offers;

namespace OpenWebServices.Tests;

public class MergedRowTests
{
    private static readonly Guid GroupA = Guid.NewGuid();
    private static readonly Guid GroupB = Guid.NewGuid();

    private static ServerOffer Offer(Guid group, string name, bool available = true) =>
        FakeServerProvider.CheapOffer with { GroupId = group, GroupName = name, Available = available };

    [Fact]
    public void Identical_offers_from_two_groups_share_one_row()
    {
        var rows = OfferRow.Merge([Offer(GroupA, "Hetzner A"), Offer(GroupB, "Hetzner B")]);

        var row = Assert.Single(rows);
        Assert.Equal(["Hetzner A", "Hetzner B"], row.GroupNames);
        Assert.Equal([GroupA, GroupB], row.Orderable.Select(o => o.GroupId));
    }

    [Fact]
    public void Offers_with_different_prices_or_billing_keep_their_own_rows()
    {
        var cheaper = Offer(GroupB, "Hetzner B") with { MonthlyNet = FakeServerProvider.CheapOffer.MonthlyNet - 1 };
        var monthly = Offer(GroupB, "Hetzner B") with { Billing = new OfferBilling("monthly", "Monthly", "Billed per month.") };

        Assert.Equal(3, OfferRow.Merge([Offer(GroupA, "Hetzner A"), cheaper, monthly]).Count);
    }

    [Fact]
    public void A_row_orders_only_from_groups_that_can_deliver_now()
    {
        var row = Assert.Single(OfferRow.Merge([Offer(GroupA, "A", available: false), Offer(GroupB, "B")]));

        Assert.True(row.Available);
        Assert.Equal(GroupB, row.Offer.GroupId);
        Assert.Equal([GroupB], row.Orderable.Select(o => o.GroupId));
    }

    private static DomainAvailability Domain(Guid group, string name, string domain = "example.com", decimal price = 9.99m) =>
        new DomainAvailability(domain, true, false, price, 12.99m, "EUR") { GroupId = group, GroupName = name };

    [Fact]
    public void Identical_domain_results_from_two_groups_share_one_row()
    {
        var rows = DomainRow.Merge([Domain(GroupA, "OVH A"), Domain(GroupB, "OVH B"), Domain(GroupA, "OVH A", "example.de")]);

        Assert.Equal(2, rows.Count);
        Assert.Equal([GroupA, GroupB], rows[0].Choices.Select(c => c.GroupId));
        Assert.Equal("example.de", rows[1].Result.Domain);
    }

    [Fact]
    public void Domain_results_with_different_prices_keep_their_own_rows() =>
        Assert.Equal(2, DomainRow.Merge([Domain(GroupA, "OVH A"), Domain(GroupB, "OVH B", price: 7.99m)]).Count);
}
