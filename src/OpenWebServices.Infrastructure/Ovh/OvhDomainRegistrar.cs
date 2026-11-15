using System.Globalization;
using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Ovh.OvhApiClient;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>
/// Domain search and orders through OVHcloud order carts: a cart is created, the domain added and configured, and the
/// cart's checkout quoted. The cart id is the quote id, so placing the order checks out exactly what was quoted.
/// </summary>
internal sealed class OvhDomainRegistrar(OvhApiClient api, string subsidiary) : IDomainRegistrar
{
    private const int MaxConcurrentChecks = 4;
    private const string CreateAction = "create";
    private const string OneYear = "P1Y";

    /// <summary>Configurations OVHcloud fills from the account when they are left empty.</summary>
    private static readonly HashSet<string> DefaultedConfigurations = new(StringComparer.Ordinal)
    {
        "OWNER_CONTACT", "ADMIN_ACCOUNT", "TECH_ACCOUNT", "DNS",
    };

    private const string LegalAge = "OWNER_LEGAL_AGE";

    /// <summary>
    /// OVHcloud's answer when it refuses to check a name: a temporary per-name limit after the same name was checked
    /// several times within minutes. Other names keep working, and the limit lifts after a while.
    /// </summary>
    private const string RefusedCheck = "check failed";

    public async Task<IReadOnlyList<DomainAvailability>> CheckAsync(IReadOnlyList<string> domains, CancellationToken ct)
    {
        var cartId = await CreateCartAsync("ows domain search", ct);
        try
        {
            var results = new DomainAvailability[domains.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, domains.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentChecks, CancellationToken = ct },
                async (i, token) => results[i] = await CheckOneAsync(cartId, domains[i], token));
            return results;
        }
        finally
        {
            await DeleteCartQuietlyAsync(cartId);
        }
    }

    public async Task<DomainQuote> QuoteAsync(string domain, bool ownerIsOfLegalAge, CancellationToken ct)
    {
        var cartId = await CreateCartAsync($"ows order {domain}", ct);
        try
        {
            await api.PostAsync($"order/cart/{Escape(cartId)}/assign", ct);
            var offer = (await GetOffersAsync(cartId, domain, ct)).FirstOrDefault(o => o.Action == CreateAction && o.Orderable)
                        ?? throw new ProviderException($"{domain} cannot be registered at OVHcloud right now.");
            var item = await api.PostAsync<OvhCartItem>(
                $"order/cart/{Escape(cartId)}/domain", new OvhAddDomain(domain, OneYear, offer.PricingMode), ct);
            await ConfigureAsync(cartId, item.ItemId, ownerIsOfLegalAge, ct);

            // GET checkout validates the cart and prices it without creating an order.
            var preview = await api.GetAsync<OvhOrder>($"order/cart/{Escape(cartId)}/checkout", ct)
                          ?? throw new ProviderException("OVHcloud returned no price for the order.");
            return ToQuote(cartId, domain, preview);
        }
        catch
        {
            await DeleteCartQuietlyAsync(cartId);
            throw;
        }
    }

    public async Task<PlacedDomainOrder> PlaceOrderAsync(string quoteId, DomainOrderOptions options, CancellationToken ct)
    {
        var order = await api.PostAsync<OvhOrder>(
            $"order/cart/{Escape(quoteId)}/checkout",
            new OvhCheckout(options.PayWithDefaultMethod, options.WaiveWithdrawalPeriod),
            ct);
        var orderId = order.OrderId?.ToString(CultureInfo.InvariantCulture)
                      ?? throw new ProviderException("OVHcloud did not return an order number. Check your orders in the OVHcloud Control Panel before trying again.");
        var total = order.Prices?.WithTax;
        return new PlacedDomainOrder(
            orderId,
            options.PayWithDefaultMethod ? null : order.Url,
            total?.Value ?? 0m,
            total?.CurrencyCode ?? "",
            options.PayWithDefaultMethod);
    }

    public async Task<IReadOnlyList<RegisteredDomain>> ListDomainsAsync(CancellationToken ct) =>
        [.. (await api.GetAllAsync<string>("domain", ct)).Select(name => new RegisteredDomain(name))];

    private async Task<DomainAvailability> CheckOneAsync(string cartId, string domain, CancellationToken ct)
    {
        List<OvhDomainOffer> offers;
        try
        {
            offers = await GetOffersAsync(cartId, domain, ct);
        }
        catch (ProviderException ex) when (ex.Message.EndsWith(RefusedCheck, StringComparison.Ordinal))
        {
            return new DomainAvailability(domain, false, false, null, null, "",
                "OVHcloud refused to check this name right now, possibly after repeated checks. Try again later or look it up in the OVHcloud Control Panel.");
        }
        catch (ProviderException ex)
        {
            return new DomainAvailability(domain, false, false, null, null, "", $"OVHcloud cannot check this name: {ex.Message}");
        }

        if (offers.FirstOrDefault(o => o.Action == CreateAction && o.Orderable) is { } create)
        {
            return new DomainAvailability(
                domain,
                Available: true,
                IsPremium: create.PricingMode?.Contains("premium", StringComparison.OrdinalIgnoreCase) == true,
                Price(create, "TOTAL") ?? Price(create, "PRICE"),
                Price(create, "RENEW"),
                Currency(create));
        }

        var note = offers.Any(o => o.Action == "transfer") ? "Registered by someone else." : "Not available at OVHcloud.";
        return new DomainAvailability(domain, false, false, null, null, offers.Select(Currency).FirstOrDefault(c => c.Length > 0) ?? "", note);
    }

    private async Task ConfigureAsync(string cartId, long itemId, bool ownerIsOfLegalAge, CancellationToken ct)
    {
        var itemPath = $"order/cart/{Escape(cartId)}/item/{itemId.ToString(CultureInfo.InvariantCulture)}";
        var required = await api.GetAllAsync<OvhRequiredConfiguration>($"{itemPath}/requiredConfiguration", ct);

        // Registry-specific data (claims notices, protected codes, local presence, ...) cannot be asked for generically.
        var unsupported = required
            .Where(c => c.Required == true && c.Label != LegalAge && !DefaultedConfigurations.Contains(c.Label))
            .Select(c => c.Label)
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new ProviderException(
                $"This extension needs registry details ows cannot provide yet ({string.Join(", ", unsupported)}). " +
                "Order it in the OVHcloud Control Panel instead.");
        }

        if (required.Any(c => c.Label == LegalAge))
            await api.PostAsync($"{itemPath}/configuration", new OvhItemConfiguration(LegalAge, ownerIsOfLegalAge ? "true" : "false"), ct);
    }

    private Task<List<OvhDomainOffer>> GetOffersAsync(string cartId, string domain, CancellationToken ct) =>
        api.GetAllAsync<OvhDomainOffer>($"order/cart/{Escape(cartId)}/domain?domain={Escape(domain)}", ct);

    private async Task<string> CreateCartAsync(string description, CancellationToken ct) =>
        (await api.PostAsync<OvhCart>("order/cart", new OvhCreateCart(subsidiary, description), ct)).CartId;

    /// <summary>Carts expire on their own after a day; deleting them early only keeps the account tidy.</summary>
    private async Task DeleteCartQuietlyAsync(string cartId)
    {
        try
        {
            await api.DeleteAsync($"order/cart/{Escape(cartId)}", CancellationToken.None);
        }
        catch (ProviderException)
        {
        }
    }

    private static DomainQuote ToQuote(string cartId, string domain, OvhOrder preview)
    {
        var net = preview.Prices?.WithoutTax;
        var gross = preview.Prices?.WithTax;
        var currency = gross?.CurrencyCode ?? net?.CurrencyCode ?? "";
        var lines = (preview.Details ?? [])
            .Where(d => d.TotalPrice is not null)
            .Select(d => new DomainQuoteLine(d.Description ?? d.Domain ?? "Item", d.TotalPrice!.Value))
            .ToList();
        return new DomainQuote(
            cartId, domain, lines, net?.Value ?? 0m, gross?.Value ?? 0m, currency,
            [.. (preview.Contracts ?? []).Select(c => new DomainContract(c.Name, c.Url))]);
    }

    private static decimal? Price(OvhDomainOffer offer, string label) =>
        offer.Prices?.FirstOrDefault(p => p.Label == label)?.Price.Value;

    private static string Currency(OvhDomainOffer offer) =>
        offer.Prices?.Select(p => p.Price.CurrencyCode).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "";
}
