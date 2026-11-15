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

    public OvhNetworkTests() => OvhNetworkProvider.PollInterval = TimeSpan.Zero;

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

    private static OvhNetworkProvider NewProvider(Handler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://eu.api.ovh.com/1.0/") };
        return new OvhNetworkProvider(new OvhApiClient(http, NullLogger<OvhApiClient>.Instance, "app", "secret", "consumer"), "p1");
    }

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
