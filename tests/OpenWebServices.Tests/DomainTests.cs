using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Domains;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Tenants;
using OpenWebServices.Infrastructure.Ovh;

namespace OpenWebServices.Tests;

public class DomainTests
{
    [Fact]
    public void Bare_names_are_expanded_with_default_extensions()
    {
        var names = DomainSearch.ExpandQuery(" Example ");

        Assert.Equal(DomainSearch.DefaultExtensions.Count, names.Count);
        Assert.Contains("example.com", names);
        Assert.Contains("example.de", names);
    }

    [Theory]
    [InlineData("https://www.Example.com/", "example.com")]
    [InlineData("bücher.de", "xn--bcher-kva.de")]
    public void Full_names_are_checked_as_typed(string query, string expected) =>
        Assert.Equal([expected], DomainSearch.ExpandQuery(query));

    [Theory]
    [InlineData("")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.com")]
    [InlineData("example.c")]
    public void Invalid_queries_are_rejected(string query) =>
        Assert.Throws<DomainException>(() => DomainSearch.ExpandQuery(query));

    [Fact]
    public async Task Search_only_asks_groups_with_domain_registrations_enabled()
    {
        var (connection, factory, groups) = Setup();
        groups.Add(TestData.Group(ResourceKind.Servers));

        var result = await new DomainSearch(groups, factory).ExecuteAsync("taken", CancellationToken.None);

        Assert.Single(connection.Registrar!.Checked);
        Assert.Empty(result.Errors);
        Assert.False(Assert.Single(result.Results, r => r.Domain == "taken.com").Available);
        Assert.Equal("taken.com", result.Results[0].Domain);
    }

    [Fact]
    public async Task Ordering_needs_the_admin_role()
    {
        var (_, factory, groups) = Setup();
        var group = groups.Items[0];

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new QuoteDomainOrder(TestData.Access(TenantRole.Member), groups, factory)
                .ExecuteAsync(group.Id, "example.com", true, CancellationToken.None));
    }

    [Fact]
    public async Task Quotes_need_the_legal_age_confirmation_and_one_full_name()
    {
        var (_, factory, groups) = Setup();
        var quote = new QuoteDomainOrder(TestData.Access(TenantRole.Admin), groups, factory);
        var groupId = groups.Items[0].Id;

        await Assert.ThrowsAsync<DomainException>(() => quote.ExecuteAsync(groupId, "example.com", false, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => quote.ExecuteAsync(groupId, "example", true, CancellationToken.None));
        Assert.Equal("example.com", (await quote.ExecuteAsync(groupId, "Example.com", true, CancellationToken.None)).Domain);
    }

    [Fact]
    public async Task Placed_orders_are_audited_and_trigger_a_sync()
    {
        var (connection, factory, groups) = Setup();
        var group = groups.Items[0];
        var audit = new FakeAuditLog();
        var sync = new FakeSyncScheduler();
        var quote = new DomainQuote("cart-1", "example.com", [], 9.99m, 11.89m, "EUR", []);

        var order = await new PlaceDomainOrder(TestData.Access(TenantRole.Admin), groups, factory, audit, new FakeUnitOfWork(), sync)
            .ExecuteAsync(group.Id, quote, new DomainOrderOptions(false, true), CancellationToken.None);

        Assert.Equal("1234", order.OrderId);
        Assert.Equal(("cart-1", new DomainOrderOptions(false, true)), Assert.Single(connection.Registrar!.Placed));
        Assert.Contains(audit.Entries, e => e.Type == AuditTypes.DomainOrdered && e.Detail.Contains("example.com"));
        Assert.Contains(group.Id, sync.Requested);
    }

    [Fact]
    public async Task Ovh_search_reports_availability_and_prices_from_a_cart()
    {
        var handler = new OvhHandler();
        handler.Responses["POST order/cart"] = """{"cartId":"c1"}""";
        handler.Responses["GET order/cart/c1/domain?domain=free.com"] = """
            [{"action":"create","orderable":true,"pricingMode":"create-default","prices":[
              {"label":"PRICE","price":{"value":10.49,"currencyCode":"EUR","text":"10.49 €"}},
              {"label":"TOTAL","price":{"value":8.99,"currencyCode":"EUR","text":"8.99 €"}},
              {"label":"RENEW","price":{"value":12.99,"currencyCode":"EUR","text":"12.99 €"}}]}]
            """;
        handler.Responses["GET order/cart/c1/domain?domain=taken.com"] = """
            [{"action":"transfer","orderable":true,"pricingMode":"transfer-default","prices":[
              {"label":"TOTAL","price":{"value":7.49,"currencyCode":"EUR"}}]}]
            """;
        handler.Responses["GET order/cart/c1/domain?domain=held.com"] =
            """[{"action":"transfer","orderable":false,"pricingMode":"transfer-default","prices":[]}]""";

        var results = await NewRegistrar(handler).CheckAsync(["free.com", "taken.com", "held.com"], CancellationToken.None);

        Assert.True(results[0].Available);
        Assert.Equal(8.99m, results[0].Price);
        Assert.Equal(12.99m, results[0].RenewalPrice);
        Assert.False(results[0].Transferable);
        Assert.False(results[1].Available);
        Assert.Equal("Registered elsewhere.", results[1].Note);
        Assert.True(results[1].Transferable);
        Assert.Equal(7.49m, results[1].TransferPrice);
        Assert.False(results[2].Transferable);
        Assert.Null(results[2].TransferPrice);
        Assert.Contains("DELETE order/cart/c1", handler.Requested);
    }

    [Fact]
    public async Task Ovh_transfer_quote_uses_the_transfer_offer_and_passes_the_auth_code()
    {
        var handler = new OvhHandler();
        handler.Responses["POST order/cart"] = """{"cartId":"c1"}""";
        handler.Responses["POST order/cart/c1/assign"] = "null";
        handler.Responses["GET order/cart/c1/domain?domain=moving.com"] = """
            [{"action":"create","orderable":false,"pricingMode":"create-default","prices":[]},
             {"action":"transfer","orderable":true,"pricingMode":"transfer-default","prices":[]}]
            """;
        handler.Responses["POST order/cart/c1/domain"] = """{"itemId":7}""";
        handler.Responses["GET order/cart/c1/item/7/requiredConfiguration"] =
            """[{"label":"AUTH_INFO","type":"String","required":true},{"label":"OWNER_LEGAL_AGE","type":"bool","required":true}]""";
        handler.Responses["POST order/cart/c1/item/7/configuration"] = """{"id":1}""";
        handler.Responses["GET order/cart/c1/checkout"] = """
            {"prices":{"withTax":{"value":8.92,"currencyCode":"EUR"},"withoutTax":{"value":7.49,"currencyCode":"EUR"}},"details":[],"contracts":[]}
            """;

        var quote = await NewRegistrar(handler).QuoteTransferAsync("moving.com", "Xy7#pQ2!", true, CancellationToken.None);

        Assert.True(quote.IsTransfer);
        Assert.Equal(8.92m, quote.TotalGross);
        Assert.Contains(handler.Bodies, b => b.Contains("\"pricingMode\":\"transfer-default\""));
        Assert.Contains(handler.Bodies, b => b.Contains("\"label\":\"AUTH_INFO\"") && b.Contains("Xy7#pQ2!"));

        // A domain OVHcloud does not accept as a transfer (locked elsewhere, too young, ...) is refused before ordering.
        handler.Responses["GET order/cart/c1/domain?domain=moving.com"] =
            """[{"action":"transfer","orderable":false,"pricingMode":"transfer-default","prices":[]}]""";
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            NewRegistrar(handler).QuoteTransferAsync("moving.com", "code", true, CancellationToken.None));
        Assert.Contains("unlocked at its current registrar", error.Message);
    }

    [Fact]
    public async Task Ovh_transfer_lock_is_changed_only_when_needed()
    {
        var handler = new OvhHandler();
        handler.Responses["GET domain/mine.com"] = """{"state":"ok","transferLockStatus":"locked"}""";
        handler.Responses["PUT domain/mine.com"] = "null";

        await NewRegistrar(handler).SetTransferLockAsync("mine.com", locked: false, CancellationToken.None);
        Assert.Contains("""{"transferLockStatus":"unlocked"}""", handler.Bodies);

        handler.Requested.Clear();
        await NewRegistrar(handler).SetTransferLockAsync("mine.com", locked: true, CancellationToken.None);
        Assert.DoesNotContain("PUT domain/mine.com", handler.Requested);

        handler.Responses["GET domain/mine.com"] = """{"state":"ok","transferLockStatus":"unavailable"}""";
        await Assert.ThrowsAsync<ProviderException>(() =>
            NewRegistrar(handler).SetTransferLockAsync("mine.com", locked: false, CancellationToken.None));
    }

    [Fact]
    public async Task Ovh_auth_code_is_only_read_from_an_unlocked_domain()
    {
        var handler = new OvhHandler();
        handler.Responses["GET domain/mine.com"] = """{"state":"ok","transferLockStatus":"locked"}""";
        handler.Responses["GET domain/mine.com/authInfo"] = "\"Xy7#pQ2!\"";

        var error = await Assert.ThrowsAsync<ProviderException>(() => NewRegistrar(handler).GetAuthCodeAsync("mine.com", CancellationToken.None));
        Assert.Contains("Unlock it first", error.Message);
        Assert.DoesNotContain("GET domain/mine.com/authInfo", handler.Requested);

        handler.Responses["GET domain/mine.com"] = """{"state":"ok","transferLockStatus":"unlocked"}""";
        Assert.Equal("Xy7#pQ2!", await NewRegistrar(handler).GetAuthCodeAsync("mine.com", CancellationToken.None));
    }

    [Theory]
    [InlineData("ok", "locked", DomainState.Active, DomainTransferLock.Locked)]
    [InlineData("pending_incoming_transfer", null, DomainState.IncomingTransfer, DomainTransferLock.Unknown)]
    [InlineData("outgoing_transfer", "unlocked", DomainState.OutgoingTransfer, DomainTransferLock.Unlocked)]
    [InlineData("expired", "unlocking", DomainState.Other, DomainTransferLock.Unlocking)]
    [InlineData(null, "unavailable", DomainState.Unknown, DomainTransferLock.NotSupported)]
    public void Ovh_domain_states_and_locks_are_mapped(string? state, string? lockStatus, DomainState expectedState, DomainTransferLock expectedLock)
    {
        Assert.Equal(expectedState, OvhDomainRegistrar.ToState(state));
        Assert.Equal(expectedLock, OvhDomainRegistrar.ToTransferLock(lockStatus));
    }

    [Fact]
    public async Task Transfer_quotes_need_the_admin_role_an_auth_code_and_one_full_name()
    {
        var (connection, factory, groups) = Setup();
        var groupId = groups.Items[0].Id;

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new QuoteDomainTransfer(TestData.Access(TenantRole.Member), groups, factory)
                .ExecuteAsync(groupId, "moving.com", "code", true, CancellationToken.None));

        var quote = new QuoteDomainTransfer(TestData.Access(TenantRole.Admin), groups, factory);
        await Assert.ThrowsAsync<DomainException>(() => quote.ExecuteAsync(groupId, "moving.com", "   ", true, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => quote.ExecuteAsync(groupId, "moving.com", "code", false, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => quote.ExecuteAsync(groupId, "moving", "code", true, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => quote.ExecuteAsync(groupId, "moving.com", new string('x', 65), true, CancellationToken.None));

        var result = await quote.ExecuteAsync(groupId, "Moving.com", "  Xy7#pQ2!\n", true, CancellationToken.None);

        Assert.True(result.IsTransfer);
        Assert.Equal(("moving.com", "Xy7#pQ2!"), Assert.Single(connection.Registrar!.TransferQuotes));
    }

    [Fact]
    public async Task Placed_transfers_are_audited_as_transfers()
    {
        var (_, factory, groups) = Setup();
        var audit = new FakeAuditLog();
        var quote = new DomainQuote("cart-t", "moving.com", [], 7.49m, 8.92m, "EUR", [], IsTransfer: true);

        await new PlaceDomainOrder(TestData.Access(TenantRole.Admin), groups, factory, audit, new FakeUnitOfWork(), new FakeSyncScheduler())
            .ExecuteAsync(groups.Items[0].Id, quote, new DomainOrderOptions(true, true), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditTypes.DomainTransferOrdered, entry.Type);
        Assert.StartsWith("Ordered the transfer of moving.com", entry.Detail);
    }

    [Fact]
    public async Task Transfer_lock_changes_need_the_admin_role_and_are_audited()
    {
        var (connection, factory, groups) = Setup();
        var groupId = groups.Items[0].Id;
        var audit = new FakeAuditLog();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new ChangeDomainTransferLock(TestData.Access(TenantRole.Member), groups, factory, audit, new FakeUnitOfWork())
                .ExecuteAsync(groupId, "mine.com", false, CancellationToken.None));

        await new ChangeDomainTransferLock(TestData.Access(TenantRole.Admin), groups, factory, audit, new FakeUnitOfWork())
            .ExecuteAsync(groupId, " Mine.com ", false, CancellationToken.None);

        Assert.Equal(("mine.com", false), Assert.Single(connection.Registrar!.LockChanges));
        Assert.Contains(audit.Entries, e => e.Type == AuditTypes.DomainTransferLockChanged && e.Detail.Contains("Unlocked mine.com"));
    }

    [Fact]
    public async Task Revealing_an_auth_code_is_audited_without_the_code()
    {
        var (_, factory, groups) = Setup();
        var groupId = groups.Items[0].Id;
        var audit = new FakeAuditLog();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new RevealDomainAuthCode(TestData.Access(TenantRole.Member), groups, factory, audit, new FakeUnitOfWork())
                .ExecuteAsync(groupId, "mine.com", CancellationToken.None));

        var code = await new RevealDomainAuthCode(TestData.Access(TenantRole.Admin), groups, factory, audit, new FakeUnitOfWork())
            .ExecuteAsync(groupId, "mine.com", CancellationToken.None);

        Assert.Equal(FakeDomainRegistrar.AuthCode, code);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditTypes.DomainAuthCodeRevealed, entry.Type);
        Assert.DoesNotContain(code, entry.Detail);
    }

    [Fact]
    public async Task Ovh_search_explains_extensions_ovh_refuses_to_check()
    {
        var handler = new OvhHandler();
        handler.Responses["POST order/cart"] = """{"cartId":"c1"}""";
        handler.BadRequests["GET order/cart/c1/domain?domain=name.com"] = "Some parameters are invalid in the request: check failed";
        handler.BadRequests["GET order/cart/c1/domain?domain=name.net"] = "Something else";

        var results = await NewRegistrar(handler).CheckAsync(["name.com", "name.net"], CancellationToken.None);

        Assert.False(results[0].Available);
        Assert.Equal("OVHcloud refused to check this name right now, possibly after repeated checks. Try again later or look it up in the OVHcloud Control Panel.", results[0].Note);
        Assert.StartsWith("OVHcloud cannot check this name:", results[1].Note);
    }

    [Fact]
    public async Task Ovh_quote_configures_legal_age_and_refuses_registry_specific_requirements()
    {
        var handler = new OvhHandler();
        handler.Responses["POST order/cart"] = """{"cartId":"c1"}""";
        handler.Responses["POST order/cart/c1/assign"] = "null";
        handler.Responses["GET order/cart/c1/domain?domain=example.com"] =
            """[{"action":"create","orderable":true,"pricingMode":"create-default","prices":[]}]""";
        handler.Responses["POST order/cart/c1/domain"] = """{"itemId":7}""";
        handler.Responses["GET order/cart/c1/item/7/requiredConfiguration"] =
            """[{"label":"OWNER_LEGAL_AGE","type":"bool","required":true},{"label":"OWNER_CONTACT","type":"/me/contact","required":false}]""";
        handler.Responses["POST order/cart/c1/item/7/configuration"] = """{"id":1,"label":"OWNER_LEGAL_AGE","value":"true"}""";
        handler.Responses["GET order/cart/c1/checkout"] = """
            {"orderId":null,"url":null,"prices":{"withTax":{"value":11.89,"currencyCode":"EUR"},"withoutTax":{"value":9.99,"currencyCode":"EUR"}},
             "details":[{"description":"example.com - 1 year","domain":"example.com","totalPrice":{"value":9.99,"currencyCode":"EUR"}}],
             "contracts":[{"name":"General terms","url":"https://registrar.example/terms.pdf"}]}
            """;

        var quote = await NewRegistrar(handler).QuoteAsync("example.com", true, CancellationToken.None);

        Assert.Equal("c1", quote.QuoteId);
        Assert.Equal(11.89m, quote.TotalGross);
        Assert.Equal("General terms", Assert.Single(quote.Contracts).Name);
        Assert.Contains(handler.Bodies, b => b.Contains("\"label\":\"OWNER_LEGAL_AGE\"") && b.Contains("\"value\":\"true\""));

        handler.Responses["GET order/cart/c1/item/7/requiredConfiguration"] =
            """[{"label":"OWNER_LEGAL_AGE","type":"bool","required":true},{"label":"CLAIMS_NOTICE","type":"String","required":true}]""";
        var error = await Assert.ThrowsAsync<ProviderException>(() => NewRegistrar(handler).QuoteAsync("example.com", true, CancellationToken.None));
        Assert.Contains("CLAIMS_NOTICE", error.Message);
    }

    [Fact]
    public async Task Cancelling_needs_the_admin_role_and_is_audited()
    {
        var (connection, factory, groups) = Setup();
        var groupId = groups.Items[0].Id;
        var audit = new FakeAuditLog();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new ChangeDomainCancellation(TestData.Access(TenantRole.Member), groups, factory, audit, new FakeUnitOfWork())
                .ExecuteAsync(groupId, "mine.com", true, CancellationToken.None));

        var change = new ChangeDomainCancellation(TestData.Access(TenantRole.Admin), groups, factory, audit, new FakeUnitOfWork());
        await change.ExecuteAsync(groupId, " Mine.com ", true, CancellationToken.None);
        await change.ExecuteAsync(groupId, "mine.com", false, CancellationToken.None);

        Assert.Equal([("mine.com", true), ("mine.com", false)], connection.Registrar!.Cancellations);
        Assert.Contains(audit.Entries, e => e.Type == AuditTypes.DomainCancelled && e.Detail.Contains("mine.com"));
        Assert.Contains(audit.Entries, e => e.Type == AuditTypes.DomainCancellationWithdrawn);
    }

    [Fact]
    public async Task Ovh_lists_domains_with_dates_renewal_and_price()
    {
        var handler = new OvhHandler();
        handler.Responses["GET domain"] = """["auto.com","ending.de","broken.net"]""";
        handler.Responses["GET domain/auto.com/serviceInfos"] = """
            {"serviceId":11,"creation":"2021-03-04","expiration":"2027-03-04","canDeleteAtExpiration":true,
             "renew":{"automatic":true,"deleteAtExpiration":false,"forced":false,"manualPayment":false,"period":12}}
            """;
        handler.Responses["GET domain/auto.com"] = """{"state":"ok","transferLockStatus":"locked"}""";
        handler.Responses["GET service/11/renew?includeOptions=false"] = """
            [{"renewPeriod":"P2Y","strategies":[{"price":{"value":25.98,"currencyCode":"EUR"},"services":[11]}]},
             {"renewPeriod":"P1Y","strategies":[{"price":{"value":12.99,"currencyCode":"EUR","text":"12.99 €"},"services":[11]}]}]
            """;
        handler.Responses["GET domain/ending.de/serviceInfos"] = """
            {"serviceId":12,"creation":"2024-01-01","expiration":"2026-12-01","canDeleteAtExpiration":true,
             "renew":{"automatic":true,"deleteAtExpiration":true,"forced":false,"period":12}}
            """;

        var domains = await NewRegistrar(handler).ListDomainsAsync(CancellationToken.None);

        var auto = domains[0];
        Assert.Equal(new DateOnly(2021, 3, 4), auto.RegisteredOn);
        Assert.Equal(new DateOnly(2027, 3, 4), auto.ExpiresOn);
        Assert.Equal(DomainRenewal.Automatic, auto.Renewal);
        Assert.Equal(12.99m, auto.YearlyPrice);
        Assert.Equal("EUR", auto.Currency);
        Assert.Equal(DomainTransferLock.Locked, auto.TransferLock);
        Assert.Equal(DomainState.Active, auto.State);
        Assert.Equal(DomainRenewal.EndsAtExpiry, domains[1].Renewal);
        Assert.Equal(DomainTransferLock.Unknown, domains[1].TransferLock);
        Assert.Null(domains[1].RenewalPrice);
        Assert.Equal("broken.net", domains[2].Name);
        Assert.NotNull(domains[2].Note);
    }

    [Fact]
    public async Task Ovh_cancellation_only_changes_the_deletion_flag()
    {
        var handler = new OvhHandler();
        handler.Responses["GET domain/mine.com/serviceInfos"] = """
            {"serviceId":11,"canDeleteAtExpiration":true,"renew":{"automatic":false,"deleteAtExpiration":false,"forced":false,"period":24}}
            """;
        handler.Responses["PUT domain/mine.com/serviceInfos"] = "null";

        await NewRegistrar(handler).SetEndAtExpiryAsync("mine.com", true, CancellationToken.None);

        Assert.Contains("PUT domain/mine.com/serviceInfos", handler.Requested);
        Assert.Contains("""{"renew":{"automatic":false,"deleteAtExpiration":true,"forced":false,"period":24}}""", handler.Bodies);

        handler.Responses["GET domain/mine.com/serviceInfos"] = """{"serviceId":11,"canDeleteAtExpiration":false}""";
        await Assert.ThrowsAsync<ProviderException>(() => NewRegistrar(handler).SetEndAtExpiryAsync("mine.com", true, CancellationToken.None));
    }

    [Theory]
    [InlineData("P1Y", 12)]
    [InlineData("P6M", 6)]
    [InlineData("P2Y", 24)]
    [InlineData("P1D", null)]
    [InlineData(null, null)]
    public void Ovh_renewal_durations_are_read_as_months(string? duration, int? months) =>
        Assert.Equal(months, OvhDomainRegistrar.Months(duration));

    private static (FakeConnection Connection, FakeConnectionFactory Factory, InMemoryGroupRepository Groups) Setup()
    {
        var connection = new FakeConnection();
        var groups = new InMemoryGroupRepository();
        groups.Add(TestData.Group(ResourceKind.Servers | ResourceKind.Domains));
        return (connection, new FakeConnectionFactory(connection), groups);
    }

    private static OvhDomainRegistrar NewRegistrar(OvhHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://eu.api.ovh.com/1.0/") };
        return new OvhDomainRegistrar(new OvhApiClient(http, NullLogger<OvhApiClient>.Instance, "app", "secret", "consumer"), "DE");
    }

    /// <summary>Answers "METHOD path?query"; auth/time and unknown DELETEs succeed, other unknown calls answer 404.</summary>
    private sealed class OvhHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];
        /// <summary>Keys answered with 400 and the given OVH error message.</summary>
        public Dictionary<string, string> BadRequests { get; } = [];
        public List<string> Requested { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.Method.Method} {request.RequestUri!.PathAndQuery["/1.0/".Length..]}";
            lock (Requested)
            {
                Requested.Add(key);
                if (request.Content is not null)
                    Bodies.Add(request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            }

            await Task.Yield();
            if (BadRequests.TryGetValue(key, out var message))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent($$"""{"message":"{{message}}"}""", Encoding.UTF8, "application/json"),
                };
            }

            var body = key == "GET auth/time" ? "1700000000"
                : Responses.TryGetValue(key, out var configured) ? configured
                : request.Method == HttpMethod.Delete ? "null"
                : null;
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
