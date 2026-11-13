using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Sync;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Tests;

public class SyncGroupTests
{
    private readonly FakeConnection _connection = new();
    private readonly InMemoryGroupRepository _groups = new();
    private readonly InMemoryServerRepository _servers = new();
    private readonly InMemoryDnsZoneRepository _zones = new();
    private readonly FakeClock _clock = new();
    private readonly ProviderGroup _group;

    public SyncGroupTests()
    {
        _group = TestData.Group();
        _groups.Add(_group);
    }

    private SyncGroup CreateSut() => new(
        _groups, new FakeConnectionFactory(_connection),
        [new ServerInventorySync(_servers, _clock), new DnsZoneInventorySync(_zones, _clock)],
        new FakeUnitOfWork(), _clock);

    private Task<SyncReport> SyncAsync() => CreateSut().ExecuteAsync(_group.Id, CancellationToken.None);

    [Fact]
    public async Task Imports_unknown_servers_and_zones()
    {
        _connection.ServerProvider.Servers.Add(TestData.Remote("1", "web"));
        _connection.DnsProvider.Zones.Add(new ProviderZone("5", "Example.COM.", "ok", 3600, 4, ["a.ns", "b.ns"], "valid"));

        var report = await SyncAsync();

        var server = Assert.Single(_servers.Items);
        Assert.Equal(ServerOrigin.Imported, server.Origin);
        Assert.Equal(ServerStatus.Running, server.Status);
        Assert.Equal(_group.Id, server.GroupId);
        var zone = Assert.Single(_zones.Items);
        Assert.Equal("example.com", zone.Name);
        Assert.Equal(["a.ns", "b.ns"], zone.NameserverList);
        Assert.True(report.Succeeded);
        Assert.Equal(_clock.UtcNow, _group.LastSyncAtUtc);
    }

    [Fact]
    public async Task Running_twice_does_not_duplicate()
    {
        _connection.ServerProvider.Servers.Add(TestData.Remote("1"));
        _connection.DnsProvider.Zones.Add(new ProviderZone("5", "example.com", "ok", null, null, [], null));

        await SyncAsync();
        await SyncAsync();

        Assert.Single(_servers.Items);
        Assert.Single(_zones.Items);
    }

    [Fact]
    public async Task Updates_known_servers_from_the_provider()
    {
        _connection.ServerProvider.Servers.Add(TestData.Remote("1"));
        await SyncAsync();

        _connection.ServerProvider.Servers[0] = TestData.Remote("1", state: ObservedServerState.Stopped);
        await SyncAsync();

        Assert.Equal(ServerStatus.Stopped, Assert.Single(_servers.Items).Status);
    }

    [Fact]
    public async Task Does_not_import_a_server_that_is_being_provisioned()
    {
        var provisioning = Server.Provision(_group.TenantId, _group.Id, "web", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR", "key", _clock.UtcNow);
        _servers.Add(provisioning);
        _connection.ServerProvider.Servers.Add(TestData.Remote("1", "web", ObservedServerState.Starting));

        await SyncAsync();

        Assert.Same(provisioning, Assert.Single(_servers.Items));
    }

    [Fact]
    public async Task Vanished_imported_servers_are_removed_but_created_ones_are_marked_failed()
    {
        var created = Server.Provision(_group.TenantId, _group.Id, "mine", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR", "key", _clock.UtcNow);
        created.AttachProviderResources("2", "7", null);
        _servers.Add(created);
        _connection.ServerProvider.Servers.Add(TestData.Remote("1"));
        await SyncAsync();

        _connection.ServerProvider.Servers.Clear();
        await SyncAsync();

        var remaining = Assert.Single(_servers.Items);
        Assert.Same(created, remaining);
        Assert.Equal(ServerStatus.Failed, remaining.Status);
        Assert.Equal(ServerInventorySync.MissingReason, remaining.FailureReason);
    }

    [Fact]
    public async Task Vanished_zones_are_removed()
    {
        _zones.Add(DnsZone.Track(_group.TenantId, _group.Id, "5", "old.example", _clock.UtcNow));

        await SyncAsync();

        Assert.Empty(_zones.Items);
    }

    [Fact]
    public async Task Only_enabled_resource_kinds_are_synced()
    {
        _group.SetEnabledResources(ResourceKind.Dns, _clock.UtcNow);
        _connection.ServerProvider.Servers.Add(TestData.Remote("1"));
        _connection.DnsProvider.Zones.Add(new ProviderZone("5", "example.com", "ok", null, null, [], null));

        await SyncAsync();

        Assert.Empty(_servers.Items);
        Assert.Single(_zones.Items);
    }

    [Fact]
    public async Task Provider_errors_are_recorded_on_the_group_instead_of_thrown()
    {
        _connection.DnsProvider.FailOnList = true;

        var report = await SyncAsync();

        Assert.False(report.Succeeded);
        Assert.Equal("dns down", _group.LastSyncError);
    }
}
