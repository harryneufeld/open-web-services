using System.Net;
using System.Text;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Servers;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;
using OpenWebServices.Infrastructure.Hetzner;

namespace OpenWebServices.Tests;

public class FirewallAndNetworkTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData(" 2001:db8::1/64 ", "2001:db8::/64")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    public void Ranges_are_normalized_to_their_network(string input, string expected) =>
        Assert.Equal(expected, IpRanges.Normalize(input));

    [Theory]
    [InlineData("example.com")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fe80::1%eth0")]
    public void Invalid_ranges_are_rejected(string input) =>
        Assert.Throws<DomainException>(() => IpRanges.Normalize(input));

    [Fact]
    public void Next_free_subnet_skips_taken_ranges()
    {
        Assert.Equal("10.0.0.0/24", IpRanges.NextFreeSubnet("10.0.0.0/16", []));
        Assert.Equal("10.0.2.0/24", IpRanges.NextFreeSubnet("10.0.0.0/16", ["10.0.0.0/24", "10.0.1.0/24"]));
        Assert.Equal("10.0.1.0/24", IpRanges.NextFreeSubnet("10.0.0.0/16", ["10.0.0.0/24", "10.0.2.0/23"]));
        Assert.Null(IpRanges.NextFreeSubnet("10.0.0.0/24", ["10.0.0.0/24"]));
    }

    [Theory]
    [InlineData("10.0.0.0/16", "10.0.0.0/16")]
    [InlineData("172.16.5.0/24", "172.16.5.0/24")]
    [InlineData("192.168.0.0/16", "192.168.0.0/16")]
    public void Private_ranges_are_accepted_for_networks(string input, string expected) =>
        Assert.Equal(expected, ServerNetworks.NormalizePrivateRange(input));

    [Theory]
    [InlineData("8.8.0.0/16")]
    [InlineData("10.0.0.0/28")]
    [InlineData("172.32.0.0/16")]
    public void Public_or_tiny_ranges_are_rejected_for_networks(string input) =>
        Assert.Throws<DomainException>(() => ServerNetworks.NormalizePrivateRange(input));

    [Theory]
    [InlineData("Mail.Example.com.", "mail.example.com")]
    [InlineData("  ", null)]
    public void Reverse_dns_host_names_are_normalized(string input, string? expected) =>
        Assert.Equal(expected, ServerNetworks.NormalizeHostName(input));

    [Fact]
    public void Reverse_dns_rejects_single_labels() =>
        Assert.Throws<DomainException>(() => ServerNetworks.NormalizeHostName("localhost"));

    [Theory]
    [InlineData("22", "22")]
    [InlineData(" 8000 - 8100 ", "8000-8100")]
    [InlineData("443-443", "443")]
    public void Ports_are_normalized(string port, string expected) =>
        Assert.Equal(expected, ServerFirewalls.Normalize(new FirewallRule(FirewallProtocol.Tcp, port, ["0.0.0.0/0"])).Port);

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("90-80")]
    [InlineData("http")]
    public void Invalid_ports_are_rejected(string port) =>
        Assert.Throws<DomainException>(() => ServerFirewalls.Normalize(new FirewallRule(FirewallProtocol.Udp, port, ["0.0.0.0/0"])));

    [Fact]
    public void Icmp_rules_drop_the_port_and_need_a_source()
    {
        Assert.Null(ServerFirewalls.Normalize(new FirewallRule(FirewallProtocol.Icmp, "22", ["::/0"])).Port);
        Assert.Throws<DomainException>(() => ServerFirewalls.Normalize(new FirewallRule(FirewallProtocol.Icmp, null, [" "])));
    }

    [Fact]
    public async Task Saving_rules_normalizes_and_deduplicates_them()
    {
        var (firewalls, connection, server) = Setup();

        await firewalls.SaveAsync(server.Id,
        [
            new FirewallRule(FirewallProtocol.Tcp, "22", ["203.0.113.7"], "Office"),
            new FirewallRule(FirewallProtocol.Tcp, "22", ["203.0.113.7/32"], "Office again"),
        ], CancellationToken.None);

        var saved = Assert.Single(connection.FirewallProvider!.Managed["1"]);
        Assert.Equal(["203.0.113.7/32"], saved.Sources);
    }

    [Fact]
    public async Task Empty_rule_lists_are_refused_because_they_block_everything()
    {
        var (firewalls, _, server) = Setup();

        await Assert.ThrowsAsync<DomainException>(() => firewalls.SaveAsync(server.Id, [], CancellationToken.None));
    }

    [Fact]
    public async Task Viewers_cannot_change_firewalls()
    {
        var (firewalls, _, server) = Setup(TenantRole.Viewer);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            firewalls.SaveAsync(server.Id, [ServerFirewalls.Presets[0].Rule], CancellationToken.None));
    }

    [Fact]
    public async Task Providers_without_firewalls_are_reported_not_thrown()
    {
        var (firewalls, connection, server) = Setup();
        connection.FirewallProvider = null;

        var view = await firewalls.GetAsync(server.Id, CancellationToken.None);

        Assert.Null(view.Firewall);
        Assert.Contains("Fake Cloud", view.Unavailable);
    }

    [Fact]
    public async Task Deleting_a_server_removes_its_firewall_and_tolerates_failures()
    {
        var connection = new FakeConnection();
        var factory = new FakeConnectionFactory(connection);
        var servers = new InMemoryServerRepository();
        var server = RunningServer();
        servers.Add(server);
        connection.FirewallProvider!.Managed["1"] = [ServerFirewalls.Presets[0].Rule];
        var delete = new DeleteServer(TestData.Access(), factory, servers, new FakeUnitOfWork());

        await delete.ExecuteAsync(server.Id, CancellationToken.None);
        Assert.Empty(connection.FirewallProvider.Managed);

        var second = RunningServer();
        servers.Add(second);
        connection.FirewallProvider.FailOnRemove = true;
        await delete.ExecuteAsync(second.Id, CancellationToken.None);
        Assert.Null(await servers.FindAsync(second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Hetzner_firewall_is_created_for_the_server_and_other_firewalls_are_reported()
    {
        HetznerApiClient.ActionPollInterval = TimeSpan.Zero;
        var handler = new HetznerHandler();
        handler.Get["firewalls"] = """
            {"firewalls":[
              {"id":5,"name":"console-made","labels":{},"rules":[{"direction":"in","protocol":"tcp","port":"80","source_ips":["0.0.0.0/0"]}],
               "applied_to":[{"type":"server","server":{"id":42}}]}],
             "meta":{"pagination":{"next_page":null}}}
            """;
        handler.Post["firewalls"] = """{"firewall":{"id":9,"name":"ows-server-42","rules":[]},"actions":[{"id":1,"status":"success"}]}""";
        var provider = new HetznerFirewallProvider(NewApi(handler));

        var before = await provider.GetServerFirewallAsync("42", CancellationToken.None);
        await provider.SetServerFirewallAsync("42", [ServerFirewalls.Presets[0].Rule], CancellationToken.None);

        Assert.Null(before.ManagedRules);
        var other = Assert.Single(before.OtherFirewalls);
        Assert.Equal("80", Assert.Single(other.InboundRules).Port);
        var body = Assert.Single(handler.Bodies, b => b.Path == "firewalls").Body;
        Assert.Contains("\"ows-server\":\"42\"", body);
        Assert.Contains("\"apply_to\":[{\"type\":\"server\",\"server\":{\"id\":42}}]", body);
        Assert.Contains("\"direction\":\"in\"", body);
        Assert.DoesNotContain("destination_ips", body);
    }

    [Fact]
    public void Hetzner_offers_are_classified_by_product_line()
    {
        Assert.Equal(ServerCategory.SharedBudget, HetznerServerProvider.Tier("cx23", "shared").Category);
        Assert.Equal("Cost-Optimized (Arm)", HetznerServerProvider.Tier("cax11", "shared").ProductLine);
        Assert.Equal(ServerCategory.SharedPerformance, HetznerServerProvider.Tier("cpx31", "shared").Category);
        Assert.Equal(ServerCategory.Dedicated, HetznerServerProvider.Tier("ccx13", "dedicated").Category);
        Assert.Equal(99.9m, HetznerServerProvider.Tier("cx23", "shared").AvailabilitySla);
    }

    [Theory]
    [InlineData("d2-2", ServerCategory.SharedBudget, "99.95")]
    [InlineData("b3-8", ServerCategory.Dedicated, "99.99")]
    [InlineData("win-c3-4", ServerCategory.ComputeOptimized, "99.99")]
    [InlineData("r3-64", ServerCategory.MemoryOptimized, "99.99")]
    [InlineData("l40s-90", ServerCategory.Gpu, null)]
    [InlineData("zz9-1", ServerCategory.Other, null)]
    public void Ovh_flavors_are_classified_by_product_line(string flavor, ServerCategory category, string? sla)
    {
        var tier = Infrastructure.Ovh.OvhServerProvider.Tier(flavor);

        Assert.Equal(category, tier.Category);
        Assert.Equal(sla is null ? null : decimal.Parse(sla, System.Globalization.CultureInfo.InvariantCulture), tier.AvailabilitySla);
    }

    private static Server RunningServer()
    {
        var server = Server.Provision(TestData.TenantId, Guid.NewGuid(), "web-1", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR", "key", TestKeys.Ed25519Line(200), Now);
        server.AttachProviderResources("1", "2", "203.0.113.10");
        server.ApplyObserved(ObservedServerState.Running, null, Now);
        return server;
    }

    private static (ServerFirewalls Firewalls, FakeConnection Connection, Server Server) Setup(TenantRole role = TenantRole.Member)
    {
        var connection = new FakeConnection();
        var servers = new InMemoryServerRepository();
        var server = RunningServer();
        servers.Add(server);
        return (new ServerFirewalls(TestData.Access(role), new FakeConnectionFactory(connection), servers), connection, server);
    }

    private static HetznerApiClient NewApi(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.hetzner.cloud/v1/") }, "token");

    /// <summary>Answers by method and path (query ignored); unknown paths answer 404.</summary>
    private sealed class HetznerHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Get { get; } = [];
        public Dictionary<string, string> Post { get; } = [];
        public List<(string Path, string Body)> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath["/v1/".Length..];
            if (request.Content is not null)
                Bodies.Add((path, await request.Content.ReadAsStringAsync(ct)));
            var table = request.Method == HttpMethod.Post ? Post : Get;
            return table.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"message":"not found"}}""") };
        }
    }
}
