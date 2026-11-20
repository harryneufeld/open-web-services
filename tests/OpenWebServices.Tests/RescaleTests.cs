using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Servers;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;
using OpenWebServices.Infrastructure.Hetzner;
using OpenWebServices.Infrastructure.Ovh;

namespace OpenWebServices.Tests;

public class RescaleTests
{
    private readonly FakeClock _clock = new();
    private readonly InMemoryServerRepository _servers = new();
    private readonly FakeConnection _connection = new();
    private readonly FakeSyncScheduler _sync = new();
    private readonly Server _server;

    public RescaleTests()
    {
        _server = Server.Provision(TestData.TenantId, Guid.NewGuid(), "web-1", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR",
            "key", TestKeys.Ed25519Line(200), _clock.UtcNow);
        _server.AttachProviderResources("42", "7", "203.0.113.10");
        _server.ApplyObserved(ObservedServerState.Running, null, _clock.UtcNow);
        _servers.Add(_server);
        _connection.ServerProvider.Servers.Add(Remote("cx23", 4m));
        HetznerApiClient.ActionPollInterval = TimeSpan.Zero;
    }

    private static ProviderServer Remote(string type, decimal price, ObservedServerState state = ObservedServerState.Running) =>
        new("42", "web-1", state, "203.0.113.10", "fsn1", type, "ubuntu-24.04", price, "EUR");

    private RescaleServer Sut(TenantRole role = TenantRole.Member) =>
        new(TestData.Access(role), new FakeConnectionFactory(_connection), _servers, _sync, new FakeUnitOfWork(), _clock);

    [Fact]
    public void A_rescale_holds_against_interim_states_until_the_type_changes()
    {
        _server.BeginRescale(_clock.UtcNow);

        _server.ApplyObservedType("cx23", 4m, "EUR");
        _server.ApplyObserved(ObservedServerState.Stopped, null, _clock.UtcNow.AddMinutes(2));
        Assert.Equal(ServerStatus.Rescaling, _server.Status);
        Assert.True(_server.IsTransitioning);

        _server.ApplyObservedType("cx33", 7m, "EUR");
        _server.ApplyObserved(ObservedServerState.Running, null, _clock.UtcNow.AddMinutes(3));
        Assert.Equal(ServerStatus.Running, _server.Status);
        Assert.Equal("cx33", _server.ServerType);
        Assert.Equal(7m, _server.MonthlyPriceNet);
    }

    [Fact]
    public void A_rescale_the_provider_never_confirms_ends_after_the_grace_period()
    {
        _server.BeginRescale(_clock.UtcNow);

        _server.ApplyObserved(ObservedServerState.Unknown, null, _clock.UtcNow + Server.RescaleGrace);

        Assert.Equal(ServerStatus.Stopped, _server.Status);
    }

    [Fact]
    public void An_unknown_price_keeps_the_last_known_one()
    {
        _server.ApplyObservedType("cx23", 0m, "EUR");

        Assert.Equal(4m, _server.MonthlyPriceNet);
    }

    [Fact]
    public void Busy_servers_cannot_be_rescaled()
    {
        _server.MarkStopping(_clock.UtcNow);

        Assert.Throws<DomainException>(() => _server.BeginRescale(_clock.UtcNow));
    }

    [Fact]
    public async Task Rescaling_moves_the_server_and_records_the_new_type_and_price()
    {
        _connection.RescaleProvider!.OnRescaled = type =>
            _connection.ServerProvider.Servers[0] = Remote(type, 7m, ObservedServerState.Starting);

        await Sut().ExecuteAsync(_server.Id, "cx33", growDisk: true, CancellationToken.None);

        Assert.Equal([("42", "cx33", true)], _connection.RescaleProvider.Calls);
        Assert.Equal("cx33", _server.ServerType);
        Assert.Equal(7m, _server.MonthlyPriceNet);
        Assert.Equal(ServerStatus.Starting, _server.Status);
        Assert.Contains(_server.GroupId, _sync.Requested);
    }

    [Fact]
    public async Task A_provider_that_is_still_resizing_keeps_the_server_rescaling()
    {
        await Sut().ExecuteAsync(_server.Id, "cx33", growDisk: false, CancellationToken.None);

        Assert.Equal(ServerStatus.Rescaling, _server.Status);
        Assert.Equal("cx23", _server.ServerType);
    }

    [Fact]
    public async Task The_disk_only_grows_on_request_where_the_provider_lets_it_keep_its_size()
    {
        _connection.RescaleProvider!.Options = _connection.RescaleProvider.Options with { DiskIsOptional = false };

        await Sut().ExecuteAsync(_server.Id, "cx33", growDisk: true, CancellationToken.None);

        Assert.False(_connection.RescaleProvider.Calls[0].GrowDisk);
    }

    [Fact]
    public async Task A_failed_rescale_shows_the_state_the_provider_reports()
    {
        _connection.RescaleProvider!.Failure = new ProviderException("server_type unavailable");

        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            Sut().ExecuteAsync(_server.Id, "cx33", growDisk: false, CancellationToken.None));

        Assert.Equal("server_type unavailable", error.Message);
        Assert.Equal(ServerStatus.Running, _server.Status);
        Assert.Equal("cx23", _server.ServerType);
    }

    [Fact]
    public async Task Only_offered_and_available_types_are_accepted()
    {
        _connection.RescaleProvider!.Options = _connection.RescaleProvider.Options with
        {
            Targets = [FakeRescaleProvider.Bigger with { Available = false }],
        };

        await Assert.ThrowsAsync<DomainException>(() => Sut().ExecuteAsync(_server.Id, "cx33", false, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => Sut().ExecuteAsync(_server.Id, "ccx63", false, CancellationToken.None));
        Assert.Empty(_connection.RescaleProvider.Calls);
        Assert.Equal(ServerStatus.Running, _server.Status);
    }

    [Fact]
    public async Task Viewers_cannot_rescale() =>
        await Assert.ThrowsAnyAsync<Exception>(() => Sut(TenantRole.Viewer).ExecuteAsync(_server.Id, "cx33", false, CancellationToken.None));

    [Fact]
    public async Task Providers_without_rescaling_say_so()
    {
        _connection.RescaleProvider = null;

        var view = await Sut().GetAsync(_server.Id, CancellationToken.None);

        Assert.Null(view.Options);
        Assert.Contains("cannot rescale", view.Unavailable, StringComparison.Ordinal);
    }

    private const string HetznerServer = """
        {"server":{"id":42,"name":"web-1","status":"running","primary_disk_size":40,
         "location":{"name":"fsn1","city":"Falkenstein","country":"DE"},
         "server_type":{"id":1,"name":"cx23","cores":2,"memory":4,"disk":40,"cpu_type":"shared","architecture":"x86",
           "prices":[{"location":"fsn1","price_monthly":{"net":"4.00","gross":"4.76"}}]}}}
        """;

    private static string HetznerType(long id, string name, int disk, string architecture = "x86", string net = "7.00",
        string location = "fsn1", string deprecation = "null") => $$$"""
        {"id":{{{id}}},"name":"{{{name}}}","cores":4,"memory":8,"disk":{{{disk}}},"cpu_type":"shared","architecture":"{{{architecture}}}",
         "prices":[{"location":"{{{location}}}","price_monthly":{"net":"{{{net}}}","gross":"1"}}],"locations":[],"deprecation":{{{deprecation}}}}
        """;

    [Fact]
    public async Task Hetzner_offers_types_of_the_same_architecture_with_enough_disk()
    {
        var handler = new Handler();
        handler.Responses["GET servers/42"] = HetznerServer;
        handler.Responses["GET server_types?per_page=50&page=1"] = "{\"server_types\":[" + string.Join(",",
            HetznerType(1, "cx23", 40, net: "4.00"),
            HetznerType(2, "cx33", 80, net: "7.00"),
            HetznerType(3, "cx13", 20, net: "3.00"),
            HetznerType(4, "cax21", 80, "arm"),
            HetznerType(5, "cpx31", 160, net: "15.00"),
            HetznerType(6, "cx63", 320, location: "nbg1"),
            HetznerType(7, "cx22", 40, deprecation: "{\"announced\":\"2025-01-01T00:00:00Z\"}")) + "]}";
        handler.Responses["GET locations?per_page=50&page=1"] = """{"locations":[{"name":"fsn1","city":"Falkenstein","country":"DE"}]}""";
        handler.Responses["GET datacenters?per_page=50&page=1"] =
            """{"datacenters":[{"location":{"name":"fsn1","city":"Falkenstein","country":"DE"},"server_types":{"available":[1,2,5],"available_for_migration":[2]}}]}""";

        var options = await new HetznerRescaleProvider(NewHetznerApi(handler)).GetRescaleOptionsAsync("42", CancellationToken.None);

        Assert.Equal(["cx33", "cpx31"], options.Targets.Select(t => t.TypeName));
        Assert.True(options.Targets[0].Available);
        Assert.False(options.Targets[1].Available); // orderable, but not as a migration target
        Assert.Equal("cx23", options.Current?.TypeName);
        Assert.Equal(40, options.DiskGb);
        Assert.True(options.StopsServer);
        Assert.True(options.DiskIsOptional);
    }

    [Fact]
    public async Task Hetzner_shuts_the_server_down_changes_the_type_and_starts_it_again()
    {
        var handler = new Handler();
        handler.Sequences["GET servers/42"] = new([HetznerServer, HetznerServer.Replace("\"running\"", "\"stopping\""), HetznerServer.Replace("\"running\"", "\"off\"")]);
        handler.Responses["POST servers/42/actions/change_type"] = """{"action":{"id":5,"status":"running"}}""";
        handler.Responses["GET actions/5"] = """{"action":{"id":5,"status":"success"}}""";

        await new HetznerRescaleProvider(NewHetznerApi(handler)).RescaleAsync("42", "cx33", growDisk: false, CancellationToken.None);

        Assert.Equal(
            ["POST servers/42/actions/shutdown", "POST servers/42/actions/change_type", "GET actions/5", "POST servers/42/actions/poweron"],
            handler.Requested.Where(r => !r.StartsWith("GET servers", StringComparison.Ordinal)));
        Assert.Contains("""{"server_type":"cx33","upgrade_disk":false}""", handler.Bodies);
    }

    [Fact]
    public async Task Hetzner_leaves_a_stopped_server_off_and_restarts_a_running_one_after_a_failed_change()
    {
        var handler = new Handler();
        handler.Responses["GET servers/42"] = HetznerServer.Replace("\"running\"", "\"off\"");
        await new HetznerRescaleProvider(NewHetznerApi(handler)).RescaleAsync("42", "cx33", growDisk: true, CancellationToken.None);
        Assert.DoesNotContain("POST servers/42/actions/shutdown", handler.Requested);
        Assert.DoesNotContain("POST servers/42/actions/poweron", handler.Requested);
        Assert.Contains("""{"server_type":"cx33","upgrade_disk":true}""", handler.Bodies);

        var failing = new Handler();
        failing.Sequences["GET servers/42"] = new([HetznerServer, HetznerServer.Replace("\"running\"", "\"off\"")]);
        failing.Responses["POST servers/42/actions/change_type"] = """{"action":{"id":5,"status":"error","error":{"code":"x","message":"no capacity"}}}""";
        await Assert.ThrowsAsync<ProviderException>(() =>
            new HetznerRescaleProvider(NewHetznerApi(failing)).RescaleAsync("42", "cx33", growDisk: false, CancellationToken.None));
        Assert.Contains("POST servers/42/actions/poweron", failing.Requested);
    }

    private const string OvhCatalog = """
        {"locale":{"currencyCode":"EUR"},"addons":[
          {"planCode":"b3-8.consumption","pricings":[{"intervalUnit":"hour","price":5000000,"tax":1000000}]},
          {"planCode":"b3-16.consumption","pricings":[{"intervalUnit":"hour","price":10000000,"tax":2000000}]},
          {"planCode":"b3-16.monthly.postpaid","pricings":[{"intervalUnit":"month","price":3000000000,"tax":600000000}]},
          {"planCode":"d2-4.consumption","pricings":[{"intervalUnit":"hour","price":1000000,"tax":200000}]},
          {"planCode":"win-b3-16.consumption","pricings":[{"intervalUnit":"hour","price":20000000,"tax":4000000}]},
          {"planCode":"b3-8-flex.consumption","pricings":[{"intervalUnit":"hour","price":6000000,"tax":1200000}]}]}
        """;

    private static string OvhFlavor(string id, string name, int disk, bool available = true) =>
        $$"""{"id":"{{id}}","name":"{{name}}","vcpus":2,"ram":8,"disk":{{disk}},"type":"ovh.ssd.eg","architecture":"x86_64","available":{{(available ? "true" : "false")}}}""";

    private static Handler OvhHandler(string planCode = "b3-8.consumption")
    {
        var handler = new Handler();
        handler.Responses["GET cloud/project/p1/instance/i1"] =
            """{"id":"i1","name":"web-1","status":"ACTIVE","region":"GRA11","planCode":"PLAN","flavor":{"id":"f8","name":"b3-8","vcpus":2,"ram":8,"disk":50,"architecture":"x86_64"}}""".Replace("PLAN", planCode, StringComparison.Ordinal);
        handler.Responses["GET cloud/project/p1/flavor?region=GRA11"] = "[" + string.Join(",",
            OvhFlavor("f8", "b3-8", 50),
            OvhFlavor("f16", "b3-16", 100),
            OvhFlavor("d4", "d2-4", 25),
            OvhFlavor("w16", "win-b3-16", 100),
            OvhFlavor("x8", "b3-8-flex", 50)) + "]";
        handler.Responses["GET order/catalog/public/cloud?ovhSubsidiary=DE"] = OvhCatalog;
        return handler;
    }

    [Fact]
    public async Task Ovh_offers_larger_flavors_of_the_same_family()
    {
        var options = await NewOvhProvider(OvhHandler()).GetRescaleOptionsAsync("i1", CancellationToken.None);

        var target = Assert.Single(options.Targets);
        Assert.Equal("b3-16", target.TypeName);
        Assert.Equal(73m, target.MonthlyNet); // 0.10 per hour × 730
        Assert.Null(target.Billing);
        Assert.Equal("b3-8", options.Current?.TypeName);
        Assert.Equal(50, options.DiskGb);
        Assert.False(options.StopsServer);
        Assert.False(options.DiskIsOptional);
    }

    [Fact]
    public async Task Ovh_prices_monthly_billed_servers_at_the_monthly_rate()
    {
        var options = await NewOvhProvider(OvhHandler("b3-8.monthly.postpaid")).GetRescaleOptionsAsync("i1", CancellationToken.None);

        var target = Assert.Single(options.Targets);
        Assert.Equal(30m, target.MonthlyNet);
        Assert.Equal("monthly", target.Billing?.Id);
    }

    [Fact]
    public async Task Ovh_resizes_to_the_flavor_id()
    {
        var handler = OvhHandler();

        await NewOvhProvider(handler).RescaleAsync("i1", "b3-16", growDisk: false, CancellationToken.None);

        Assert.Contains("POST cloud/project/p1/instance/i1/resize", handler.Requested);
        Assert.Contains("""{"flavorId":"f16"}""", handler.Bodies);
    }

    private static HetznerApiClient NewHetznerApi(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.hetzner.cloud/v1/") }, "token");

    private static OvhServerProvider NewOvhProvider(Handler handler) =>
        new(new OvhApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://eu.api.ovh.com/1.0/") },
            NullLogger<OvhApiClient>.Instance, "app", "secret", "consumer"), "p1", "DE", "test-" + Guid.NewGuid());

    /// <summary>Answers "METHOD path?query" for OVH (/1.0/) and Hetzner (/v1/); writes succeed, unknown reads answer 404.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];
        public Dictionary<string, Queue<string>> Sequences { get; } = [];
        public List<string> Requested { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.PathAndQuery;
            var path = uri.StartsWith("/1.0/", StringComparison.Ordinal) ? uri["/1.0/".Length..] : uri["/v1/".Length..];
            var key = $"{request.Method.Method} {path}";
            if (key != "GET auth/time")
                Requested.Add(key);
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(ct));

            var body = key == "GET auth/time" ? "1700000000"
                : Sequences.TryGetValue(key, out var queue) && queue.Count > 0 ? (queue.Count > 1 ? queue.Dequeue() : queue.Peek())
                : Responses.TryGetValue(key, out var configured) ? configured
                : request.Method == HttpMethod.Get ? null
                : """{"action":{"id":99,"status":"success"}}""";
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
