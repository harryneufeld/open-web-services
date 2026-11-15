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
    private const string TransferAction = "transfer";
    private const string OneYear = "P1Y";

    /// <summary>The cart configuration that carries the auth code of an incoming transfer.</summary>
    private const string AuthInfo = "AUTH_INFO";

    private const string Locked = "locked";
    private const string Unlocked = "unlocked";

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

    public Task<DomainQuote> QuoteAsync(string domain, bool ownerIsOfLegalAge, CancellationToken ct) =>
        QuoteCoreAsync(domain, CreateAction, authCode: null, ownerIsOfLegalAge, ct);

    public Task<DomainQuote> QuoteTransferAsync(string domain, string authCode, bool ownerIsOfLegalAge, CancellationToken ct) =>
        QuoteCoreAsync(domain, TransferAction, authCode, ownerIsOfLegalAge, ct);

    public async Task SetTransferLockAsync(string domain, bool locked, CancellationToken ct)
    {
        var service = await GetServiceAsync(domain, ct);
        switch (service.TransferLockStatus)
        {
            case "unavailable":
                throw new ProviderException($"The registry of {domain} has no transfer lock; its auth code alone allows a transfer.");
            case Locked or "locking" when locked:
            case Unlocked or "unlocking" when !locked:
                return;
        }

        await api.PutAsync($"domain/{Escape(domain)}", new OvhUpdateTransferLock(locked ? Locked : Unlocked), ct);
    }

    public async Task<string> GetAuthCodeAsync(string domain, CancellationToken ct)
    {
        var service = await GetServiceAsync(domain, ct);
        if (service.TransferLockStatus is Locked or "locking")
            throw new ProviderException($"{domain} is locked against transfers. Unlock it first, then read the auth code.");
        if (service.TransferLockStatus == "unlocking")
            throw new ProviderException($"OVHcloud is still unlocking {domain}. Try again in a few minutes.");

        var code = await api.GetAsync<string>($"domain/{Escape(domain)}/authInfo", ct);
        return string.IsNullOrWhiteSpace(code)
            ? throw new ProviderException($"OVHcloud returned no auth code for {domain}. Some registries send it to the domain holder by e-mail instead.")
            : code;
    }

    private async Task<OvhDomainService> GetServiceAsync(string domain, CancellationToken ct) =>
        await api.GetAsync<OvhDomainService>($"domain/{Escape(domain)}", ct)
        ?? throw new ProviderException($"OVHcloud returned no details for {domain}.");

    private async Task<DomainQuote> QuoteCoreAsync(string domain, string action, string? authCode, bool ownerIsOfLegalAge, CancellationToken ct)
    {
        var transfer = action == TransferAction;
        var cartId = await CreateCartAsync(transfer ? $"ows transfer {domain}" : $"ows order {domain}", ct);
        try
        {
            await api.PostAsync($"order/cart/{Escape(cartId)}/assign", ct);
            var offer = (await GetOffersAsync(cartId, domain, ct)).FirstOrDefault(o => o.Action == action && o.Orderable)
                        ?? throw new ProviderException(transfer
                            ? $"OVHcloud does not accept a transfer of {domain} right now. Check that it is registered, unlocked at its current registrar and older than 60 days."
                            : $"{domain} cannot be registered at OVHcloud right now.");
            var item = await api.PostAsync<OvhCartItem>(
                $"order/cart/{Escape(cartId)}/domain", new OvhAddDomain(domain, OneYear, offer.PricingMode), ct);
            await ConfigureAsync(cartId, item.ItemId, ownerIsOfLegalAge, authCode, ct);

            // GET checkout validates the cart and prices it without creating an order.
            var preview = await api.GetAsync<OvhOrder>($"order/cart/{Escape(cartId)}/checkout", ct)
                          ?? throw new ProviderException("OVHcloud returned no price for the order.");
            return ToQuote(cartId, domain, preview) with { IsTransfer = transfer };
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

    public async Task<IReadOnlyList<RegisteredDomain>> ListDomainsAsync(CancellationToken ct)
    {
        var names = await api.GetAllAsync<string>("domain", ct);
        var domains = new RegisteredDomain[names.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, names.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentChecks, CancellationToken = ct },
            async (i, token) => domains[i] = await DescribeAsync(names[i], token));
        return domains;
    }

    public async Task SetEndAtExpiryAsync(string domain, bool endAtExpiry, CancellationToken ct)
    {
        var path = $"domain/{Escape(domain)}/serviceInfos";
        var info = await api.GetAsync<OvhServiceInfos>(path, ct)
                   ?? throw new ProviderException($"OVHcloud returned no service details for {domain}.");
        if (endAtExpiry && info.CanDeleteAtExpiration == false)
        {
            throw new ProviderException(
                $"OVHcloud does not allow cancelling {domain} right now, for example while a renewal or transfer is in progress. Try again later or use the OVHcloud Control Panel.");
        }

        // Only the deletion flag changes; the renewal mode and period stay as they are.
        var renew = info.Renew ?? new OvhRenew(Automatic: true, DeleteAtExpiration: false, Forced: false, ManualPayment: null, Period: null);
        await api.PutAsync(path, new OvhUpdateServiceInfos(new OvhUpdateRenew(renew.Automatic, endAtExpiry, renew.Forced, renew.Period)), ct);
    }

    /// <summary>A domain with its dates and renewal price; details OVHcloud does not return stay empty.</summary>
    private async Task<RegisteredDomain> DescribeAsync(string name, CancellationToken ct)
    {
        OvhServiceInfos? info;
        try
        {
            info = await api.GetAsync<OvhServiceInfos>($"domain/{Escape(name)}/serviceInfos", ct);
        }
        catch (ProviderException ex)
        {
            return new RegisteredDomain(name, Note: $"OVHcloud returned no details: {ex.Message}");
        }

        if (info is null)
            return new RegisteredDomain(name, Note: "OVHcloud returned no details.");

        var renewal = info.Renew switch
        {
            null => DomainRenewal.Unknown,
            { DeleteAtExpiration: true } => DomainRenewal.EndsAtExpiry,
            { Automatic: true, ManualPayment: not true } => DomainRenewal.Automatic,
            _ => DomainRenewal.Manual,
        };
        var domain = new RegisteredDomain(
            name,
            info.Creation,
            info.Expiration,
            renewal,
            CanEndAtExpiry: info.CanDeleteAtExpiration ?? true);

        // The transfer lock and state are details too: without them the transfer actions are just not offered.
        try
        {
            if (await api.GetAsync<OvhDomainService>($"domain/{Escape(name)}", ct) is { } service)
                domain = domain with { TransferLock = ToTransferLock(service.TransferLockStatus), State = ToState(service.State) };
        }
        catch (ProviderException)
        {
        }

        // The price is a detail: without it the domain is still listed. The service's own billing price is the one
        // paid for the current term (often a first-year promotion), so the price comes from the renewal offers.
        try
        {
            var offers = await api.GetAllAsync<OvhRenewDescription>(
                $"service/{info.ServiceId.ToString(CultureInfo.InvariantCulture)}/renew?includeOptions=false", ct);
            var currentMonths = info.Renew?.Period is > 0 and var period ? (int)period : 12;
            var offer = offers.FirstOrDefault(o => Months(o.RenewPeriod) == currentMonths)
                        ?? offers.FirstOrDefault(o => Months(o.RenewPeriod) is not null);
            var strategy = offer?.Strategies?.FirstOrDefault(s => s.Services is null || s.Services.Contains(info.ServiceId));
            if (strategy?.Price is { } price)
            {
                return domain with
                {
                    RenewalPrice = price.Value,
                    Currency = price.CurrencyCode ?? "",
                    RenewalMonths = Months(offer!.RenewPeriod),
                };
            }
        }
        catch (ProviderException)
        {
        }

        return domain;
    }

    internal static DomainTransferLock ToTransferLock(string? status) => status switch
    {
        Locked => DomainTransferLock.Locked,
        "locking" => DomainTransferLock.Locking,
        Unlocked => DomainTransferLock.Unlocked,
        "unlocking" => DomainTransferLock.Unlocking,
        "unavailable" => DomainTransferLock.NotSupported,
        _ => DomainTransferLock.Unknown,
    };

    internal static DomainState ToState(string? state) => state switch
    {
        null => DomainState.Unknown,
        "ok" or "autorenew_in_progress" or "autorenew_registry_in_progress" => DomainState.Active,
        "pending_incoming_transfer" => DomainState.IncomingTransfer,
        "outgoing_transfer" => DomainState.OutgoingTransfer,
        _ => DomainState.Other,
    };

    /// <summary>"P1Y" → 12, "P6M" → 6, anything else → null.</summary>
    internal static int? Months(string? duration)
    {
        if (duration is not { Length: > 2 } || duration[0] != 'P')
            return null;
        var unit = duration[^1];
        if (!int.TryParse(duration.AsSpan(1, duration.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
            return null;
        return unit switch { 'Y' => count * 12, 'M' => count, _ => null };
    }

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

        var currency = offers.Select(Currency).FirstOrDefault(c => c.Length > 0) ?? "";
        if (offers.FirstOrDefault(o => o.Action == TransferAction) is { } transfer)
        {
            return new DomainAvailability(domain, false, false, null, Price(transfer, "RENEW"), currency, "Registered elsewhere.",
                Transferable: transfer.Orderable,
                TransferPrice: transfer.Orderable ? Price(transfer, "TOTAL") ?? Price(transfer, "PRICE") : null);
        }

        return new DomainAvailability(domain, false, false, null, null, currency, "Not available at OVHcloud.");
    }

    private async Task ConfigureAsync(string cartId, long itemId, bool ownerIsOfLegalAge, string? authCode, CancellationToken ct)
    {
        var itemPath = $"order/cart/{Escape(cartId)}/item/{itemId.ToString(CultureInfo.InvariantCulture)}";
        var required = await api.GetAllAsync<OvhRequiredConfiguration>($"{itemPath}/requiredConfiguration", ct);

        // Registry-specific data (claims notices, protected codes, local presence, ...) cannot be asked for generically.
        var unsupported = required
            .Where(c => c.Required == true && c.Label != LegalAge && !DefaultedConfigurations.Contains(c.Label))
            .Where(c => authCode is null || c.Label != AuthInfo)
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

        // Extensions without auth codes (e.g. .uk, which moves by registrar tag) do not ask for one.
        if (authCode is not null && required.Any(c => c.Label == AuthInfo))
            await api.PostAsync($"{itemPath}/configuration", new OvhItemConfiguration(AuthInfo, authCode), ct);
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
