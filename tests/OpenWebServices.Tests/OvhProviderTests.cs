using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Infrastructure.Ovh;

namespace OpenWebServices.Tests;

public class OvhProviderTests
{
    private const string Project = "cloud/project/p1";

    private const string Forbidden = "403:{\"message\":\"This call has not been granted\"}";

    private const string Catalog = """
        {"locale":{"currencyCode":"EUR"},"addons":[
          {"planCode":"b3-8.consumption","pricings":[{"intervalUnit":"hour","price":5120000,"tax":972800}]},
          {"planCode":"b3-8.monthly.postpaid","pricings":[{"intervalUnit":"month","price":2800000000,"tax":532000000}]}]}
        """;

    [Fact]
    public async Task Offers_skip_regions_without_compute()
    {
        var handler = new RoutingHandler(new()
        {
            ["auth/time"] = "1700000000",
            ["order/catalog/public/cloud?ovhSubsidiary=DE"] = Catalog,
            // BHS is an object-storage region: its flavor list is not configured and answers 404.
            [$"{Project}/region"] = """["GRA11","BHS"]""",
            [$"{Project}/flavor?region=GRA11"] =
                """[{"id":"f1","name":"b3-8","vcpus":2,"ram":8,"disk":50,"planCodes":{"hourly":"b3-8.consumption"},"available":true}]""",
        });

        var offers = await NewProvider(handler).GetOffersAsync(CancellationToken.None);

        Assert.All(offers, offer => Assert.Equal("GRA11", offer.Location));
        var hourly = Assert.Single(offers, offer => offer.Billing is null);
        Assert.Equal("EUR", hourly.Currency);
        Assert.Equal(0.0512m * 730m, hourly.MonthlyNet);
        var monthly = Assert.Single(offers, offer => offer.Billing is not null);
        Assert.Equal("monthly", monthly.Billing!.Id);
        Assert.Equal(28m, monthly.MonthlyNet);
        Assert.NotEqual(hourly.Key, monthly.Key);
        Assert.DoesNotContain(handler.Requested, path => path.Contains("/region/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Offers_fail_loudly_when_flavors_are_not_granted()
    {
        var handler = new RoutingHandler(new()
        {
            ["auth/time"] = "1700000000",
            ["order/catalog/public/cloud?ovhSubsidiary=DE"] = Catalog,
            [$"{Project}/region"] = """["GRA11"]""",
            [$"{Project}/flavor?region=GRA11"] = Forbidden,
        });

        await Assert.ThrowsAsync<ProviderException>(() => NewProvider(handler).GetOffersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Refused_calls_name_the_request_and_ovh_reason()
    {
        var handler = new RoutingHandler(new()
        {
            ["auth/time"] = "1700000000",
            ["order/catalog/public/cloud?ovhSubsidiary=DE"] = Catalog,
            [$"{Project}/instance"] = Forbidden,
        });

        var error = await Assert.ThrowsAsync<ProviderException>(() => NewProvider(handler).ListServersAsync(CancellationToken.None));

        Assert.Contains($"GET {Project}/instance (403)", error.Message);
        Assert.Contains("This call has not been granted", error.Message);
    }

    [Fact]
    public async Task Servers_report_the_public_ipv4_address()
    {
        var handler = new RoutingHandler(new()
        {
            ["auth/time"] = "1700000000",
            ["order/catalog/public/cloud?ovhSubsidiary=DE"] = Catalog,
            [$"{Project}/instance"] = """
                [{"id":"i1","name":"web-1","status":"ACTIVE","region":"GRA11","flavor":{"id":"f1","name":"b3-8","vcpus":2,"ram":8,"disk":50},
                  "ipAddresses":[{"ip":"10.0.0.5","type":"private","version":4},{"ip":"203.0.113.7","type":"public","version":4}]}]
                """,
        });

        var server = Assert.Single(await NewProvider(handler).ListServersAsync(CancellationToken.None));

        Assert.Equal("203.0.113.7", server.Ipv4);
        Assert.Equal(ObservedServerState.Running, server.State);
    }

    [Theory]
    [InlineData("ACTIVE", ObservedServerState.Running)]
    [InlineData("SHUTOFF", ObservedServerState.Stopped)]
    [InlineData("SHELVED_OFFLOADED", ObservedServerState.Stopped)]
    [InlineData("HARD_REBOOT", ObservedServerState.Starting)]
    [InlineData("DELETING", ObservedServerState.Stopping)]
    [InlineData("ERROR", ObservedServerState.Unknown)]
    public void Instance_states_map_to_observed_states(string status, ObservedServerState expected) =>
        Assert.Equal(expected, OvhServerProvider.MapStatus(status));

    [Theory]
    [InlineData("GRA11", "Gravelines", "FR")]
    [InlineData("DE1", "Frankfurt", "DE")]
    [InlineData("RBX-A", "Roubaix", "FR")]
    [InlineData("EU-WEST-PAR", "Paris", "FR")]
    [InlineData("EU-WEST-LZ-BRU-A", "Brussels", "BE")]
    [InlineData("US-EAST-VA-1", "Vint Hill", "US")]
    [InlineData("XYZ9", "XYZ9", "")]
    public void Region_codes_get_readable_names(string region, string city, string country) =>
        Assert.Equal((city, country), OvhRegions.Describe(region));

    [Fact]
    public void Requests_are_signed_with_the_ovh_scheme()
    {
        var signature = OvhApiClient.Sign("secret", "consumer", "GET", "https://eu.api.ovh.com/1.0/me", "", 1700000000);

        Assert.StartsWith("$1$", signature);
        Assert.Equal(3 + 40, signature.Length);
        Assert.Equal(signature, signature.ToLowerInvariant());
    }

    private static OvhServerProvider NewProvider(RoutingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://eu.api.ovh.com/1.0/") };
        var api = new OvhApiClient(http, NullLogger<OvhApiClient>.Instance, "app", "secret", "consumer");
        // A unique endpoint per test keeps the shared price cache from leaking between tests.
        return new OvhServerProvider(api, "p1", "DE", Guid.NewGuid().ToString());
    }

    private sealed class RoutingHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        private const string Base = "/1.0/";

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery[Base.Length..];
            lock (Requested)
                Requested.Add(path);
            // A configured body of "403:{...}" answers with that status; unknown paths answer 404.
            var (status, body) = !responses.TryGetValue(path, out var configured)
                ? (HttpStatusCode.NotFound, """{"message":"not found"}""")
                : configured.Length > 4 && configured[3] == ':' && int.TryParse(configured[..3], out var code)
                    ? ((HttpStatusCode)code, configured[4..])
                    : (HttpStatusCode.OK, configured);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }
}
