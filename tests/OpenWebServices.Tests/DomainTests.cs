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
        handler.Responses["GET order/cart/c1/domain?domain=taken.com"] =
            """[{"action":"transfer","orderable":true,"pricingMode":"transfer-default","prices":[]}]""";

        var results = await NewRegistrar(handler).CheckAsync(["free.com", "taken.com"], CancellationToken.None);

        Assert.True(results[0].Available);
        Assert.Equal(8.99m, results[0].Price);
        Assert.Equal(12.99m, results[0].RenewalPrice);
        Assert.False(results[1].Available);
        Assert.Equal("Registered by someone else.", results[1].Note);
        Assert.Contains("DELETE order/cart/c1", handler.Requested);
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
