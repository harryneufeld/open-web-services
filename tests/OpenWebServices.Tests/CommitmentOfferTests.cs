using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Offers;

namespace OpenWebServices.Tests;

public class CommitmentOfferTests
{
    private static readonly ServerOffer MonthlyOffer = FakeServerProvider.CheapOffer with
    {
        MonthlyNet = 3.00m,
        Billing = new OfferBilling("monthly", "Monthly", "Billed per month."),
    };

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task Groups_choose_whether_commitment_offers_are_listed(bool show, int expected)
    {
        var connection = new FakeConnection();
        connection.ServerProvider.Offers.Add(MonthlyOffer);
        var groups = new InMemoryGroupRepository();
        var group = TestData.Group();
        group.SetShowCommitmentOffers(show, DateTime.UtcNow);
        groups.Add(group);

        var result = await new GetServerOffers(groups, new FakeConnectionFactory(connection)).ExecuteAsync(CancellationToken.None);

        Assert.Equal(expected, result.Offers.Count);
    }

    [Fact]
    public void New_groups_list_commitment_offers() => Assert.True(TestData.Group().ShowCommitmentOffers);
}
