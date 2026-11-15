namespace OpenWebServices.Application.Abstractions;

/// <summary>Whether a domain name can be registered at one registrar, and for how much.</summary>
/// <param name="Price">First-year price, net, or null when the registrar quotes none.</param>
/// <param name="RenewalPrice">Yearly price after the first year, net.</param>
/// <param name="IsPremium">The registry sets a higher, name-dependent price.</param>
/// <param name="Note">Why the name cannot be registered, or other remarks from the registrar.</param>
public sealed record DomainAvailability(
    string Domain,
    bool Available,
    bool IsPremium,
    decimal? Price,
    decimal? RenewalPrice,
    string Currency,
    string? Note = null)
{
    /// <summary>The group the name can be ordered with. Set by the application, not by the registrar.</summary>
    public Guid GroupId { get; init; }

    public string GroupName { get; init; } = "";
}

public sealed record DomainQuoteLine(string Description, decimal Net);

/// <summary>A link to terms the customer accepts by placing the order.</summary>
public sealed record DomainContract(string Name, string Url);

/// <summary>
/// A prepared, not yet placed domain order with its exact price. Placing it is a separate step so the user sees the
/// total and the terms first.
/// </summary>
/// <param name="QuoteId">Registrar-defined handle, passed back to place the order.</param>
public sealed record DomainQuote(
    string QuoteId,
    string Domain,
    IReadOnlyList<DomainQuoteLine> Lines,
    decimal TotalNet,
    decimal TotalGross,
    string Currency,
    IReadOnlyList<DomainContract> Contracts);

/// <param name="PayWithDefaultMethod">Charge the account's default payment method right away.</param>
/// <param name="WaiveWithdrawalPeriod">Start the registration now instead of after the statutory withdrawal period.</param>
public sealed record DomainOrderOptions(bool PayWithDefaultMethod, bool WaiveWithdrawalPeriod);

/// <param name="PaymentUrl">Where the order can be paid, when it was not paid automatically.</param>
public sealed record PlacedDomainOrder(string OrderId, string? PaymentUrl, decimal TotalGross, string Currency, bool PaidAutomatically);

/// <summary>What happens to a registered domain when its current term ends.</summary>
public enum DomainRenewal
{
    /// <summary>The registrar did not say.</summary>
    Unknown,

    /// <summary>Renewed and charged automatically.</summary>
    Automatic,

    /// <summary>Renewed only when someone pays the renewal.</summary>
    Manual,

    /// <summary>Cancelled: the registration ends at expiry and the name is released.</summary>
    EndsAtExpiry,
}

/// <summary>A domain the account holds. Details the registrar could not provide stay null and <see cref="Note"/> says why.</summary>
/// <param name="RegisteredOn">When the domain was first registered.</param>
/// <param name="ExpiresOn">End of the paid term.</param>
/// <param name="RenewalPrice">Net price of one renewal, covering <see cref="RenewalMonths"/>.</param>
/// <param name="CanEndAtExpiry">The registrar accepts a cancellation at expiry right now.</param>
public sealed record RegisteredDomain(
    string Name,
    DateOnly? RegisteredOn = null,
    DateOnly? ExpiresOn = null,
    DomainRenewal Renewal = DomainRenewal.Unknown,
    decimal? RenewalPrice = null,
    int? RenewalMonths = null,
    string Currency = "",
    bool CanEndAtExpiry = false,
    string? Note = null)
{
    /// <summary>The renewal price for twelve months, or null when it is unknown.</summary>
    public decimal? YearlyPrice => RenewalPrice is { } price && RenewalMonths is > 0 and var months ? price * 12m / months : null;
}

/// <summary>A registrar: searches, prices and orders domain names, and lists those the account holds.</summary>
public interface IDomainRegistrar
{
    Task<IReadOnlyList<DomainAvailability>> CheckAsync(IReadOnlyList<string> domains, CancellationToken ct);

    /// <param name="ownerIsOfLegalAge">The user confirmed that the domain holder is of legal age; most registries require it.</param>
    Task<DomainQuote> QuoteAsync(string domain, bool ownerIsOfLegalAge, CancellationToken ct);

    Task<PlacedDomainOrder> PlaceOrderAsync(string quoteId, DomainOrderOptions options, CancellationToken ct);

    /// <summary>Domains the account holds, with their dates, renewal mode and renewal price where available.</summary>
    Task<IReadOnlyList<RegisteredDomain>> ListDomainsAsync(CancellationToken ct);

    /// <summary>
    /// Cancels a domain at the end of its paid term (<paramref name="endAtExpiry"/> true), or withdraws that
    /// cancellation so it renews again. Nothing is refunded; until expiry the domain keeps working.
    /// </summary>
    Task SetEndAtExpiryAsync(string domain, bool endAtExpiry, CancellationToken ct);
}
