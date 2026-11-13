using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Application.Offers;

public sealed record OffersResult(IReadOnlyList<ServerOffer> Offers, IReadOnlyList<string> Errors);

/// <summary>Collects offers from every group that manages servers, cheapest first.</summary>
public sealed class GetServerOffers(IProviderGroupRepository groups, IProviderConnectionFactory connections)
{
    public async Task<OffersResult> ExecuteAsync(CancellationToken ct)
    {
        var offers = new List<ServerOffer>();
        var errors = new List<string>();
        foreach (var group in (await groups.ListAsync(ct)).Where(g => g.IsEnabled(ResourceKind.Servers)))
        {
            try
            {
                var provider = (await connections.ConnectAsync(group.Id, ct)).RequireServers();
                offers.AddRange((await provider.GetOffersAsync(ct))
                    .Select(o => o with { GroupId = group.Id, GroupName = group.Name }));
            }
            catch (Exception ex) when (ex is ProviderException or ProviderNotConfiguredException or CapabilityNotSupportedException)
            {
                errors.Add($"{group.Name}: {ex.Message}");
            }
        }

        return new OffersResult(
            [.. offers.OrderBy(o => o.MonthlyNet).ThenBy(o => o.GroupName).ThenBy(o => o.Location)],
            errors);
    }
}
