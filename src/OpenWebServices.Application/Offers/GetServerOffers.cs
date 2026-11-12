using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Application.Offers;

/// <summary>Collects offers from every configured provider, cheapest first.</summary>
public sealed class GetServerOffers(IHostingProviderFactory providers)
{
    public async Task<IReadOnlyList<ServerOffer>> ExecuteAsync(CancellationToken ct)
    {
        var offers = new List<ServerOffer>();
        foreach (var providerId in providers.ProviderIds)
        {
            try
            {
                var provider = await providers.GetAsync(providerId, ct);
                offers.AddRange(await provider.GetOffersAsync(ct));
            }
            catch (ProviderNotConfiguredException)
            {
                // Providers without credentials simply offer nothing yet.
            }
        }

        return [.. offers.OrderBy(o => o.MonthlyNet).ThenBy(o => o.ProviderId).ThenBy(o => o.Location)];
    }
}
