using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Infrastructure.Ovh;

namespace OpenWebServices.Tests;

public class OvhNetworkTests
{
    private const string Project = "cloud/project/p1";

    private const string Instance = """
        {"id":"i1","name":"web-1","status":"ACTIVE","region":"GRA11",
         "ipAddresses":[{"ip":"203.0.113.7","type":"public","version":4,"networkId":"ext"},
                        {"ip":"2001:db8::7","type":"public","version":6,"networkId":"ext"},
                        {"ip":"10.0.0.5","type":"private","version":4,"networkId":"os-gra"}]}
        """;

    public OvhNetworkTests()
    {
        OvhNetworkProvider.PollInterval = TimeSpan.Zero;
        OvhFirewallProvider.PollInterval = TimeSpan.Zero;
    }

    [Fact]
    public void Firewall_layout_frames_user_rules_with_replies_and_a_final_deny()
    {
        var layout = OvhFirewallProvider.Layout(
        [
            new FirewallRule(FirewallProtocol.Tcp, "22", ["203.0.113.0/24", "198.51.100.7/32", "::/0"]),
            new FirewallRule(FirewallProtocol.Tcp, "8000-8100", ["0.0.0.0/0"]),
        ]);

        Assert.Equal("established", layout[0].TcpOption!.Option);
        Assert.Equal(53, layout[1].SourcePort);
        Assert.Equal(("deny", "ipv4"), (layout[19].Action, layout[19].Protocol));
        Assert.Equal("203.0.113.0/24", layout[3].Source);
        Assert.Equal("198.51.100.7/32", layout[4].Source);
        Assert.Null(layout[5].Source);
        Assert.Equal(new OvhRange(8000, 8100), layout[5].DestinationPortRange);
        Assert.False(layout.ContainsKey(6)); // the IPv6 source takes no slot
    }

    [Fact]
    public void Firewall_refuses_more_sources_than_slots()
    {
        var sources = Enumerable.Range(1, 17).Select(i => $"203.0.113.{i}/32").ToList();

        Assert.Throws<ProviderException>(() => OvhFirewallProvider.Layout([new FirewallRule(FirewallProtocol.Tcp, "22", sources)]));
    }

    [Theory]
    [InlineData("eq 22", "22")]
    [InlineData("range 8000 8100", "8000-8100")]
    [InlineData(null, null)]
    public void Firewall_ports_are_read_from_ovh_text(string? text, string? expected) =>
        Assert.Equal(expected, OvhFirewallProvider.ParsePort(text));

    [Fact]
    public void Firewall_rules_for_the_same_port_merge_their_sources_on_read()
    {
        var rules = OvhFirewallProvider.FromOvh(
        [
            new OvhFirewallRule(0, "permit", "tcp", "any", null, null, "established", "ok"),
            new OvhFirewallRule(3, "permit", "tcp", "203.0.113.0/24", "eq 22", null, null, "ok"),
            new OvhFirewallRule(4, "permit", "tcp", "198.51.100.7/32", "eq 22", null, null, "ok"),
            new OvhFirewallRule(5, "permit", "icmp", "any", null, null, null, "ok"),
            new OvhFirewallRule(19, "deny", "ipv4", "any", null, null, null, "ok"),
        ]);

        Assert.Equal(2, rules.Count);
        Assert.Equal(["203.0.113.0/24", "198.51.100.7/32"], rules[0].Sources);
        Assert.Equal((FirewallProtocol.Icmp, "0.0.0.0/0"), (rules[1].Protocol, rules[1].Sources[0]));
    }

    [Fact]
    public async Task Firewall_is_turned_on_after_its_rules_are_written()
    {
        var handler = new Handler();
        handler.Responses[$"GET {Project}/instance/i1"] = Instance;
        handler.Responses["GET ip/203.0.113.7%2F32/firewall/203.0.113.7/rule"] = "[]";
        handler.Responses["GET ip/203.0.113.7%2F32/firewall/203.0.113.7/rule?state=creationPending"] = "[]";
        var provider = new OvhFirewallProvider(NewApi(handler), "p1");

        await provider.SetServerFirewallAsync("i1", [new FirewallRule(FirewallProtocol.Tcp, "22", ["0.0.0.0/0"])], CancellationToken.None);

        var firewallAdded = handler.Requested.IndexOf("POST ip/203.0.113.7%2F32/firewall");
        var rulesWritten = handler.Requested.Count(r => r == "POST ip/203.0.113.7%2F32/firewall/203.0.113.7/rule");
        var enabled = handler.Requested.IndexOf("PUT ip/203.0.113.7%2F32/firewall/203.0.113.7");
        Assert.True(firewallAdded >= 0 && enabled > firewallAdded);
        Assert.Equal(5, rulesWritten); // three reply rules, one user rule, the final deny
        Assert.Equal("""{"enabled":true}""", handler.Bodies[^1]);
    }

    [Fact]
    public async Task Networking_reads_reverse_dns_and_names_private_networks()
    {
        var handler = new Handler();
        handler.Responses[$"GET {Project}/instance/i1"] = Instance;
        handler.Responses["GET ip/203.0.113.7%2F32/reverse/203.0.113.7"] = """{"ipReverse":"203.0.113.7","reverse":"web.example.com."}""";
        handler.Responses[$"GET {Project}/network/private"] =
            """[{"id":"pn-1_5","name":"backend","status":"ACTIVE","type":"private","vlanId":5,"regions":[{"region":"GRA11","status":"ACTIVE","openstackId":"os-gra"}]}]""";

        var net = await NewProvider(handler).GetServerNetworkingAsync("i1", CancellationToken.None);

        Assert.Equal("203.0.113.7", net.PublicIpv4);
        Assert.Equal("web.example.com", net.ReverseDns.Single(r => r.Ip == "203.0.113.7").Ptr);
        Assert.Null(net.ReverseDns.Single(r => r.Ip == "2001:db8::7").Ptr);
        var attachment = Assert.Single(net.PrivateNetworks);
        Assert.Equal(("pn-1_5", "backend", "10.0.0.5"), (attachment.NetworkId, attachment.NetworkName, attachment.Ip));
    }

    [Fact]
    public async Task Reverse_dns_is_set_with_a_trailing_dot_and_removed_with_delete()
    {
        var handler = new Handler();
        var provider = NewProvider(handler);

        await provider.SetReverseDnsAsync("i1", "2001:db8::7", "mail.example.com", CancellationToken.None);
        await provider.SetReverseDnsAsync("i1", "203.0.113.7", null, CancellationToken.None);

        Assert.Contains("POST ip/2001%3Adb8%3A%3A7%2F128/reverse", handler.Requested);
        Assert.Contains(handler.Bodies, b => b.Contains("\"reverse\":\"mail.example.com.\""));
        Assert.Contains("DELETE ip/203.0.113.7%2F32/reverse/203.0.113.7", handler.Requested);
    }

    [Fact]
    public async Task Joining_activates_the_region_adds_a_pool_and_creates_an_interface()
    {
        var handler = new Handler();
        handler.Responses[$"GET {Project}/instance/i1"] = Instance;
        // The network exists in SBG5 only; after activating GRA11 the next read lists both regions.
        handler.Sequences[$"GET {Project}/network/private/pn-1_5"] = new Queue<string>(
        [
            """{"id":"pn-1_5","name":"backend","regions":[{"region":"SBG5","status":"ACTIVE","openstackId":"os-sbg"}]}""",
            """{"id":"pn-1_5","name":"backend","regions":[{"region":"SBG5","status":"ACTIVE","openstackId":"os-sbg"},{"region":"GRA11","status":"ACTIVE","openstackId":"os-gra"}]}""",
        ]);
        handler.Responses[$"GET {Project}/network/private/pn-1_5/subnet"] =
            """[{"id":"s1","cidr":"10.0.0.0/16","ipPools":[{"network":"10.0.0.0/16","region":"SBG5","start":"10.0.0.2","end":"10.0.0.254"}]}]""";

        await NewProvider(handler).AttachToPrivateNetworkAsync("i1", "pn-1_5", CancellationToken.None);

        Assert.Contains($"POST {Project}/network/private/pn-1_5/region", handler.Requested);
        Assert.Contains(handler.Bodies, b => b.Contains("\"region\":\"GRA11\"") && b.Contains("\"start\":\"10.0.1.2\"") && b.Contains("\"end\":\"10.0.1.254\""));
        Assert.Contains(handler.Bodies, b => b == """{"networkId":"os-gra"}""");
    }

    [Fact]
    public void Pools_take_the_next_free_slash_24() =>
        Assert.Equal(("10.0.2.2", "10.0.2.254"), OvhNetworkProvider.NextPool("10.0.0.0/16",
        [
            new OvhIpPool("10.0.0.0/16", "SBG5", "10.0.0.2", "10.0.0.254"),
            new OvhIpPool("10.0.0.0/16", "DE1", "10.0.1.2", "10.0.1.254"),
        ]));

    private static OvhNetworkProvider NewProvider(Handler handler) => new(NewApi(handler), "p1");

    private static OvhApiClient NewApi(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://eu.api.ovh.com/1.0/") }, NullLogger<OvhApiClient>.Instance, "app", "secret", "consumer");

    /// <summary>Answers "METHOD path"; writes and unknown DELETEs succeed, unknown reads answer 404.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];
        public Dictionary<string, Queue<string>> Sequences { get; } = [];
        public List<string> Requested { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.Method.Method} {request.RequestUri!.AbsoluteUri[request.RequestUri.AbsoluteUri.IndexOf("/1.0/", StringComparison.Ordinal)..]["/1.0/".Length..]}";
            Requested.Add(key);
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(ct));

            var body = key == "GET auth/time" ? "1700000000"
                : Sequences.TryGetValue(key, out var queue) && queue.Count > 0 ? (queue.Count > 1 ? queue.Dequeue() : queue.Peek())
                : Responses.TryGetValue(key, out var configured) ? configured
                : request.Method == HttpMethod.Get ? null
                : "{}";
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
