using System.Globalization;
using System.Text.RegularExpressions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Domains;

public sealed record DomainSearchResult(IReadOnlyList<DomainAvailability> Results, IReadOnlyList<string> Errors);

public sealed record GroupDomains(Guid GroupId, string GroupName, IReadOnlyList<RegisteredDomain> Domains, string? Error);

/// <summary>Domain names, searched and ordered at every group whose provider is a registrar.</summary>
public sealed partial class DomainSearch(IProviderGroupRepository groups, IProviderConnectionFactory connections)
{
    /// <summary>Extensions checked when the search has none, e.g. "example" → example.com, example.de, ...</summary>
    public static IReadOnlyList<string> DefaultExtensions { get; } = ["com", "net", "org", "de", "eu", "io", "dev", "app"];

    public async Task<DomainSearchResult> ExecuteAsync(string query, CancellationToken ct)
    {
        var names = ExpandQuery(query);
        var results = new List<DomainAvailability>();
        var errors = new List<string>();
        foreach (var group in await RegistrarGroupsAsync(groups, ct))
        {
            try
            {
                var registrar = (await connections.ConnectAsync(group.Id, ct)).RequireDomains();
                results.AddRange((await registrar.CheckAsync(names, ct))
                    .Select(r => r with { GroupId = group.Id, GroupName = group.Name }));
            }
            catch (Exception ex) when (ex is ProviderException or ProviderNotConfiguredException or CapabilityNotSupportedException)
            {
                errors.Add($"{group.Name}: {ex.Message}");
            }
        }

        // The searched order of extensions first, then available before taken, then cheapest.
        return new DomainSearchResult(
            [.. results.OrderBy(r => names.ToList().IndexOf(r.Domain)).ThenByDescending(r => r.Available).ThenBy(r => r.Price ?? decimal.MaxValue)],
            errors);
    }

    /// <summary>Lowercases, converts international names to ASCII and adds default extensions to a bare name.</summary>
    public static IReadOnlyList<string> ExpandQuery(string query)
    {
        var text = query.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "https://", "http://", "www." })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                text = text[prefix.Length..];
        }

        text = text.TrimEnd('/', '.');
        if (text.Length == 0)
            throw new DomainException("Enter a name to search, e.g. example or example.com.");

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(text);
        }
        catch (ArgumentException)
        {
            throw new DomainException($"'{query}' is not a valid domain name.");
        }

        var names = ascii.Contains('.') ? [ascii] : DefaultExtensions.Select(e => $"{ascii}.{e}").ToList();
        if (names.Any(n => n.Length > 253 || !ValidDomain().IsMatch(n)))
            throw new DomainException($"'{query}' is not a valid domain name. Use letters, digits and hyphens, e.g. example.com.");
        return names;
    }

    internal static async Task<IReadOnlyList<ProviderGroup>> RegistrarGroupsAsync(IProviderGroupRepository groups, CancellationToken ct) =>
        [.. (await groups.ListAsync(ct)).Where(g => g.IsEnabled(ResourceKind.Domains)).OrderBy(g => g.Name)];

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+(xn--[a-z0-9-]{1,59}|[a-z]{2,63})$")]
    private static partial Regex ValidDomain();
}

public sealed class DomainQueries(IProviderGroupRepository groups, IProviderConnectionFactory connections)
{
    /// <summary>Groups in which domains can be searched and ordered.</summary>
    public Task<IReadOnlyList<ProviderGroup>> ListRegistrarGroupsAsync(CancellationToken ct) =>
        DomainSearch.RegistrarGroupsAsync(groups, ct);

    /// <summary>Domains each registrar group holds, read live.</summary>
    public async Task<IReadOnlyList<GroupDomains>> ListRegisteredAsync(CancellationToken ct)
    {
        var result = new List<GroupDomains>();
        foreach (var group in await DomainSearch.RegistrarGroupsAsync(groups, ct))
        {
            try
            {
                var registrar = (await connections.ConnectAsync(group.Id, ct)).RequireDomains();
                var domains = await registrar.ListDomainsAsync(ct);
                result.Add(new GroupDomains(group.Id, group.Name, [.. domains.OrderBy(d => d.Name, StringComparer.Ordinal)], null));
            }
            catch (Exception ex) when (ex is ProviderException or ProviderNotConfiguredException or CapabilityNotSupportedException)
            {
                result.Add(new GroupDomains(group.Id, group.Name, [], ex.Message));
            }
        }

        return result;
    }
}

/// <summary>
/// Prepares an order at the registrar and returns its exact price and terms without placing it. Ordering spends money,
/// so it needs the Admin role, like managing the provider groups that pay for it.
/// </summary>
public sealed class QuoteDomainOrder(AccessContext access, IProviderGroupRepository groups, IProviderConnectionFactory connections)
{
    public async Task<DomainQuote> ExecuteAsync(Guid groupId, string domain, bool ownerIsOfLegalAge, CancellationToken ct)
    {
        access.Require(TenantRole.Admin);
        if (!ownerIsOfLegalAge)
            throw new DomainException("Confirm that the domain holder is of legal age; registries require it.");
        var name = DomainSearch.ExpandQuery(domain) is [var single] && single.Contains('.')
            ? single
            : throw new DomainException("Choose one domain name including its extension, e.g. example.com.");

        var registrar = await RegistrarAsync(groups, connections, groupId, ct);
        return await registrar.QuoteAsync(name, ownerIsOfLegalAge, ct);
    }

    internal static async Task<IDomainRegistrar> RegistrarAsync(
        IProviderGroupRepository groups, IProviderConnectionFactory connections, Guid groupId, CancellationToken ct)
    {
        var group = await groups.FindAsync(groupId, ct) ?? throw new NotFoundException("Provider group");
        if (!group.IsEnabled(ResourceKind.Domains))
            throw new DomainException($"Domain registrations are not enabled for the group '{group.Name}'.");
        return (await connections.ConnectAsync(groupId, ct)).RequireDomains();
    }
}

/// <summary>Places a prepared order. The registrar charges the account behind the group.</summary>
public sealed class PlaceDomainOrder(
    AccessContext access,
    IProviderGroupRepository groups,
    IProviderConnectionFactory connections,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    ISyncScheduler syncScheduler)
{
    public async Task<PlacedDomainOrder> ExecuteAsync(Guid groupId, DomainQuote quote, DomainOrderOptions options, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var registrar = await QuoteDomainOrder.RegistrarAsync(groups, connections, groupId, ct);
        var order = await registrar.PlaceOrderAsync(quote.QuoteId, options, ct);

        var total = order.TotalGross.ToString("0.00", CultureInfo.InvariantCulture);
        audit.Add(new AuditEntry(
            AuditTypes.DomainOrdered,
            $"Ordered {quote.Domain}: order {order.OrderId}, {total} {order.Currency} gross" +
            (order.PaidAutomatically ? ", paid with the default payment method" : ", awaiting payment"),
            access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);

        // Registrars usually create a DNS zone with the domain; the next sync imports it.
        syncScheduler.RequestSync(groupId);
        return order;
    }
}
