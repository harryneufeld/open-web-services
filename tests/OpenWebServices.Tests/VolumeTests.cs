using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Access;
using OpenWebServices.Application.Servers;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;
using OpenWebServices.Infrastructure.Hetzner;
using OpenWebServices.Infrastructure.Ovh;

namespace OpenWebServices.Tests;

public class VolumeTests
{
    private const string Device = "/dev/disk/by-id/scsi-0HC_Volume_7";
    private readonly FakeClock _clock = new();
    private readonly FakeRemoteShell _shell = new();
    private readonly InMemoryServerRepository _servers = new();
    private readonly FakeConnection _connection = new();
    private readonly Server _server;

    public VolumeTests()
    {
        var pair = new FakeKeyGenerator().Generate("ows-web-1");
        _server = Server.Provision(TestData.TenantId, Guid.NewGuid(), "web-1", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR",
            "enc:" + pair.PrivateKeyPem, ServerAccessWriter.AutomationLine(pair.PublicKeyOpenSsh), _clock.UtcNow);
        _server.AttachProviderResources("42", "7", "203.0.113.10");
        _server.ApplyObserved(ObservedServerState.Running, null, _clock.UtcNow);
        _servers.Add(_server);
        _shell.AuthorizedKeys = pair.PublicKeyOpenSsh + "\n";
        OvhVolumeProvider.PollInterval = TimeSpan.Zero;
        HetznerApiClient.ActionPollInterval = TimeSpan.Zero;
    }

    private FakeVolumeProvider Volumes => _connection.VolumeProvider!;

    private ServerVolumes Sut(TenantRole role = TenantRole.Member) => new(
        TestData.Access(role), new FakeConnectionFactory(_connection), _servers, _shell,
        new ServerAccessWriter(new InMemoryGrantRepository(), new InMemorySshKeyRepository(), _shell, new ReversingSecretProtector(), _clock));

    [Theory]
    [InlineData("data", "/mnt/data")]
    [InlineData(" Backups 2026 ", "/mnt/backups-2026")]
    [InlineData("---", "/mnt/volume")]
    public void Default_mount_points_live_below_mnt(string name, string expected) =>
        Assert.Equal(expected, VolumeMounts.DefaultMountPoint(name));

    [Theory]
    [InlineData("/mnt/data/", "/mnt/data")]
    [InlineData("/var/lib/docker", "/var/lib/docker")]
    [InlineData("/srv/backups", "/srv/backups")]
    public void Mount_points_are_normalized(string input, string expected) =>
        Assert.Equal(expected, VolumeMounts.NormalizeMountPoint(input));

    [Theory]
    [InlineData("/")]
    [InlineData("/mnt")]
    [InlineData("/etc")]
    [InlineData("/usr/local/data")]
    [InlineData("mnt/data")]
    [InlineData("/mnt/../etc")]
    [InlineData("/mnt/my data")]
    [InlineData("/mnt/x;reboot")]
    public void System_or_unsafe_mount_points_are_rejected(string input) =>
        Assert.Throws<DomainException>(() => VolumeMounts.NormalizeMountPoint(input));

    [Fact]
    public void Scripts_refuse_device_paths_that_could_break_out_of_quotes() =>
        Assert.Throws<DomainException>(() => VolumeMounts.MountScript("/dev/sdb'; reboot; '", "/mnt/data"));

    [Fact]
    public void Status_output_is_parsed_per_device()
    {
        var status = VolumeMounts.ParseStatus(
            $"{Device}|1|ext4|10501771264 1073741824 /mnt/data\n/dev/disk/by-id/b|1||\n/dev/disk/by-id/c|0||\n");

        Assert.Equal(new VolumeMount(true, "ext4", "/mnt/data", 10501771264, 1073741824), status[Device]);
        Assert.Equal(new VolumeMount(true, null, null, null, null), status["/dev/disk/by-id/b"]);
        Assert.False(status["/dev/disk/by-id/c"].DevicePresent);
    }

    [Fact]
    public void The_mount_script_formats_only_volumes_without_any_signature()
    {
        var script = VolumeMounts.MountScript(Device, "/mnt/data");

        Assert.Contains("if [ $rc -eq 2 ]; then", script, StringComparison.Ordinal);
        Assert.Contains("defaults,nofail", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_with_ssh_access_mounts_the_volume_where_asked()
    {
        var message = await Sut().CreateAsync(_server.Id, "data", 20, null, "/srv/data", CancellationToken.None);

        var request = Assert.Single(Volumes.Created);
        Assert.Equal(new CreateVolumeRequest("data", 20, "standard", "42", Format: false), request);
        Assert.Contains(_shell.Commands, c => c.Contains($"dev='{Device}'; mnt='/srv/data'", StringComparison.Ordinal));
        Assert.StartsWith("data: ", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_without_ssh_access_lets_the_provider_format_it()
    {
        _server.ApplyObserved(ObservedServerState.Stopped, null, _clock.UtcNow);

        var message = await Sut().CreateAsync(_server.Id, "data", 20, null, null, CancellationToken.None);

        Assert.True(Assert.Single(Volumes.Created).Format);
        Assert.Empty(_shell.Commands);
        Assert.Contains("mount it there yourself", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewers_cannot_create_volumes() =>
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut(TenantRole.Viewer).CreateAsync(_server.Id, "data", 20, null, null, CancellationToken.None));

    [Fact]
    public async Task The_view_shows_usage_prices_and_attachable_volumes()
    {
        Volumes.Add(new ProviderVolume("7", "data", 20, "fsn1", "42", "standard", "available", Device));
        Volumes.Add(new ProviderVolume("8", "spare", 10, "fsn1", null, "standard", "available", "/dev/disk/by-id/scsi-0HC_Volume_8"));
        Volumes.Add(new ProviderVolume("9", "far", 10, "hel1", null, "standard", "available", null));
        _shell.Respond = c => c.Contains("findmnt -n -b", StringComparison.Ordinal)
            ? new CommandResult(0, $"{Device}|1|ext4|21474836480 1073741824 /mnt/data\n")
            : null;

        var view = await Sut().GetAsync(_server.Id, CancellationToken.None);

        var row = Assert.Single(view.Attached);
        Assert.Equal("/mnt/data", row.Mount!.MountPoint);
        Assert.Equal(0.88m, row.MonthlyPriceNet);
        Assert.Equal("8", Assert.Single(view.Attachable).ExternalId);
        Assert.True(view.CanMount);
    }

    [Fact]
    public async Task Volumes_never_shrink()
    {
        Volumes.Add(new ProviderVolume("7", "data", 20, "fsn1", "42", "standard", "available", Device));

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            Sut().SaveAsync(_server.Id, "7", new VolumeChange("data", 10, null), CancellationToken.None));
        Assert.Contains("only grow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_renames_and_grows_the_filesystem_in_place()
    {
        Volumes.Add(new ProviderVolume("7", "data", 20, "fsn1", "42", "standard", "available", Device));

        var message = await Sut().SaveAsync(_server.Id, "7", new VolumeChange("db", 40, null), CancellationToken.None);

        Assert.Equal(["rename 7 db", "resize 7 40"], Volumes.Calls);
        Assert.Contains(_shell.Commands, c => c.Contains("resize2fs", StringComparison.Ordinal));
        Assert.StartsWith("db has 40 GB now. ", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Providers_that_grow_detached_volumes_get_a_detach_and_remount_around_the_resize()
    {
        Volumes.Options = Volumes.Options with { ResizesWhileAttached = false };
        Volumes.Add(new ProviderVolume("7", "data", 20, "fsn1", "42", "standard", "in-use", Device));
        _shell.Respond = c => c.Contains("findmnt -n -b", StringComparison.Ordinal)
            ? new CommandResult(0, $"{Device}|1|ext4|21474836480 1073741824 /srv/data\n")
            : null;

        await Sut().SaveAsync(_server.Id, "7", new VolumeChange("data", 40, null), CancellationToken.None);

        Assert.Equal(["detach 7", "resize 7 40", "attach 7 42"], Volumes.Calls);
        var scripts = _shell.Commands.Select(c => c.Contains("umount", StringComparison.Ordinal) ? "unmount"
            : c.Contains("mkfs.ext4", StringComparison.Ordinal) ? "mount"
            : c.Contains("resize2fs", StringComparison.Ordinal) ? "grow" : "status").ToList();
        Assert.Equal(["status", "unmount", "mount", "grow"], scripts);
        Assert.Contains(_shell.Commands, c => c.Contains("mnt='/srv/data'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detaching_unmounts_first_and_a_busy_mount_stops_it()
    {
        Volumes.Add(new ProviderVolume("7", "data", 20, "fsn1", "42", "standard", "in-use", Device));
        _shell.Respond = c => c.Contains("umount", StringComparison.Ordinal)
            ? new CommandResult(6, "/mnt/data is still in use. Stop the programs that use it, then try again.")
            : null;

        var error = await Assert.ThrowsAsync<DomainException>(() => Sut().DetachAsync(_server.Id, "7", CancellationToken.None));

        Assert.StartsWith("/mnt/data is still in use", error.Message, StringComparison.Ordinal);
        Assert.Empty(Volumes.Calls);
    }

    [Fact]
    public async Task Deleting_unmounts_detaches_and_deletes()
    {
        Volumes.Add(new ProviderVolume("7", "data", 20, "fsn1", "42", "standard", "in-use", Device));

        await Sut().DeleteAsync(_server.Id, "7", CancellationToken.None);

        Assert.Equal(["detach 7", "delete 7"], Volumes.Calls);
        Assert.Contains(_shell.Commands, c => c.Contains("umount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hetzner_creates_attached_volumes_and_waits_for_both_actions()
    {
        var handler = new Handler();
        handler.Responses["POST volumes"] = """
            {"volume":{"id":7,"name":"data","size":20,"server":42,"location":{"name":"fsn1","city":"Falkenstein","country":"DE"},"status":"creating"},
             "action":{"id":1,"status":"running"},"next_actions":[{"id":2,"status":"running"}]}
            """;
        handler.Responses["GET actions/1"] = """{"action":{"id":1,"status":"success"}}""";
        handler.Responses["GET actions/2"] = """{"action":{"id":2,"status":"success"}}""";
        handler.Responses["GET volumes/7"] = """
            {"volume":{"id":7,"name":"data","size":20,"server":42,"location":{"name":"fsn1","city":"Falkenstein","country":"DE"},
             "linux_device":"/dev/disk/by-id/scsi-0HC_Volume_7","status":"available"}}
            """;
        var provider = new HetznerVolumeProvider(new HetznerApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.hetzner.cloud/v1/") }, "token"));

        var volume = await provider.CreateVolumeAsync(new CreateVolumeRequest("data", 20, "standard", "42", Format: false), CancellationToken.None);

        Assert.Equal(Device, volume.DevicePath);
        Assert.Equal("42", volume.ServerId);
        Assert.Contains("\"server\":42", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"automount\":false", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("format", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("GET actions/2", handler.Requested);
    }

    [Fact]
    public async Task Hetzner_options_price_volumes_per_gb()
    {
        var handler = new Handler();
        handler.Responses["GET servers/42"] = """{"server":{"id":42,"name":"web-1","status":"running","location":{"name":"fsn1","city":"Falkenstein","country":"DE"}}}""";
        handler.Responses["GET pricing"] = """{"pricing":{"currency":"EUR","volume":{"price_per_gb_month":{"net":"0.0440","gross":"0.0524"}}}}""";
        var provider = new HetznerVolumeProvider(new HetznerApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.hetzner.cloud/v1/") }, "token"));

        var options = await provider.GetOptionsAsync("42", CancellationToken.None);

        Assert.Equal("fsn1", options.Location);
        Assert.Equal(0.044m, Assert.Single(options.Types).PricePerGbMonthNet);
        Assert.True(options.ResizesWhileAttached);
    }

    [Fact]
    public async Task Ovh_creates_in_the_instance_zone_waits_and_attaches()
    {
        var handler = new Handler();
        handler.Responses["GET cloud/project/p1/instance/i1"] = """{"id":"i1","name":"web-1","status":"ACTIVE","region":"EU-WEST-PAR","availabilityZone":"eu-west-par-a"}""";
        handler.Responses["POST cloud/project/p1/volume"] = """{"id":"0123456789abcdefghijklmnop","name":"data","size":20,"region":"EU-WEST-PAR","status":"creating"}""";
        handler.Sequences["GET cloud/project/p1/volume/0123456789abcdefghijklmnop"] = new(
        [
            """{"id":"0123456789abcdefghijklmnop","name":"data","size":20,"region":"EU-WEST-PAR","status":"creating"}""",
            """{"id":"0123456789abcdefghijklmnop","name":"data","size":20,"region":"EU-WEST-PAR","status":"available"}""",
            """{"id":"0123456789abcdefghijklmnop","name":"data","size":20,"region":"EU-WEST-PAR","status":"in-use","attachedTo":["i1"],"type":"high-speed"}""",
        ]);
        var provider = NewOvhProvider(handler);

        var volume = await provider.CreateVolumeAsync(new CreateVolumeRequest("data", 20, "high-speed", "i1", Format: false), CancellationToken.None);

        Assert.Equal("/dev/disk/by-id/virtio-0123456789abcdefghij", volume.DevicePath);
        Assert.Equal("i1", volume.ServerId);
        Assert.Contains("\"availabilityZone\":\"eu-west-par-a\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"type\":\"high-speed\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("POST cloud/project/p1/volume/0123456789abcdefghijklmnop/attach", handler.Requested);
        Assert.Equal("""{"instanceId":"i1"}""", handler.Bodies[1]);
    }

    [Fact]
    public async Task Ovh_options_list_the_region_types_with_catalog_prices()
    {
        var handler = new Handler();
        handler.Responses["GET cloud/project/p1/instance/i1"] = """{"id":"i1","name":"web-1","status":"ACTIVE","region":"GRA11"}""";
        handler.Responses["GET cloud/project/p1/region/GRA11/volumeType"] = """[{"name":"classic"},{"name":"high-speed-gen2"},{"name":"something-new"}]""";
        handler.Responses["GET order/catalog/public/cloud?ovhSubsidiary=XX"] = """
            {"locale":{"currencyCode":"EUR"},"addons":[
              {"planCode":"volume.classic.monthly.postpaid","pricings":[{"intervalUnit":"month","price":4200000,"tax":0}]},
              {"planCode":"volume.high-speed-gen2.consumption","pricings":[{"intervalUnit":"none","price":11900,"tax":0}]}]}
            """;
        var provider = NewOvhProvider(handler, subsidiary: "XX");

        var options = await provider.GetOptionsAsync("i1", CancellationToken.None);

        Assert.Equal(["classic", "high-speed-gen2"], options.Types.Select(t => t.Id));
        Assert.Equal(0.042m, options.Types[0].PricePerGbMonthNet);
        Assert.Equal(0.08687m, options.Types[1].PricePerGbMonthNet);
        Assert.False(options.ResizesWhileAttached);
        Assert.True(options.CanChangeType);
    }

    [Fact]
    public async Task Ovh_changes_the_type_through_the_region_endpoint()
    {
        var handler = new Handler();
        handler.Sequences["GET cloud/project/p1/volume/v1"] = new(
        [
            """{"id":"v1","name":"data","size":20,"region":"GRA11","status":"in-use","type":"classic"}""",
            """{"id":"v1","name":"data","size":20,"region":"GRA11","status":"retyping","type":"classic"}""",
            """{"id":"v1","name":"data","size":20,"region":"GRA11","status":"in-use","type":"high-speed"}""",
        ]);

        await NewOvhProvider(handler).ChangeTypeAsync("v1", "high-speed", CancellationToken.None);

        Assert.Contains("PUT cloud/project/p1/region/GRA11/volume/v1", handler.Requested);
        Assert.Equal("""{"type":"high-speed"}""", handler.Bodies[0]);
    }

    private static OvhVolumeProvider NewOvhProvider(Handler handler, string subsidiary = "FR") =>
        new(new OvhApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://eu.api.ovh.com/1.0/") },
            NullLogger<OvhApiClient>.Instance, "app", "secret", "consumer"), "p1", subsidiary, "test-" + Guid.NewGuid());

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

internal sealed class FakeVolumeProvider : IVolumeProvider
{
    private readonly List<ProviderVolume> _volumes = [];
    private int _nextId = 7;

    public VolumeOptions Options { get; set; } = new("fsn1", 10, 10240,
        [new VolumeTypeOption("standard", "Standard", null, 0.044m)], "EUR", CanChangeType: false, ResizesWhileAttached: true);

    public List<CreateVolumeRequest> Created { get; } = [];
    public List<string> Calls { get; } = [];

    public void Add(ProviderVolume volume) => _volumes.Add(volume);

    public Task<VolumeOptions> GetOptionsAsync(string externalServerId, CancellationToken ct) => Task.FromResult(Options);

    public Task<IReadOnlyList<ProviderVolume>> ListVolumesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderVolume>>(_volumes.ToList());

    public Task<ProviderVolume> CreateVolumeAsync(CreateVolumeRequest request, CancellationToken ct)
    {
        Created.Add(request);
        var id = (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var volume = new ProviderVolume(id, request.Name, request.SizeGb, Options.Location, request.ServerId, request.Type, "available",
            $"/dev/disk/by-id/scsi-0HC_Volume_{id}");
        _volumes.Add(volume);
        return Task.FromResult(volume);
    }

    public Task AttachAsync(string volumeId, string externalServerId, CancellationToken ct) =>
        Change(volumeId, $"attach {volumeId} {externalServerId}", v => v with { ServerId = externalServerId });

    public Task DetachAsync(string volumeId, CancellationToken ct) => Change(volumeId, $"detach {volumeId}", v => v with { ServerId = null });

    public Task RenameAsync(string volumeId, string name, CancellationToken ct) => Change(volumeId, $"rename {volumeId} {name}", v => v with { Name = name });

    public Task ResizeAsync(string volumeId, int sizeGb, CancellationToken ct) => Change(volumeId, $"resize {volumeId} {sizeGb}", v => v with { SizeGb = sizeGb });

    public Task ChangeTypeAsync(string volumeId, string type, CancellationToken ct) => Change(volumeId, $"retype {volumeId} {type}", v => v with { Type = type });

    public Task DeleteAsync(string volumeId, CancellationToken ct)
    {
        Calls.Add($"delete {volumeId}");
        _volumes.RemoveAll(v => v.ExternalId == volumeId);
        return Task.CompletedTask;
    }

    private Task Change(string volumeId, string call, Func<ProviderVolume, ProviderVolume> change)
    {
        Calls.Add(call);
        var index = _volumes.FindIndex(v => v.ExternalId == volumeId);
        _volumes[index] = change(_volumes[index]);
        return Task.CompletedTask;
    }
}
