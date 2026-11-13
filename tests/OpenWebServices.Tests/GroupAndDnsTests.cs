using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Dns;
using OpenWebServices.Application.Groups;
using OpenWebServices.Application.Sync;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Tests;

public class AddProviderGroupTests
{
    private readonly FakeConnection _connection = new();
    private readonly InMemoryGroupRepository _groups = new();
    private readonly InMemoryServerRepository _servers = new();
    private readonly InMemoryDnsZoneRepository _zones = new();
    private readonly FakeClock _clock = new();

    private AddProviderGroup CreateSut()
    {
        var factory = new FakeConnectionFactory(_connection);
        var sync = new SyncGroup(_groups, factory,
            [new ServerInventorySync(_servers, _clock), new DnsZoneInventorySync(_zones, _clock)], new FakeUnitOfWork(), _clock);
        return new AddProviderGroup(factory, factory, _groups, new ReversingSecretProtector(), sync, new FakeUnitOfWork(), _clock);
    }

    private static AddProviderGroupCommand Command(string token = "secret", ResourceKind kinds = ResourceKind.Servers) =>
        new("Production", "fake", new Dictionary<string, string> { ["api_token"] = token, ["ignored"] = "x" }, kinds);

    [Fact]
    public async Task Stores_encrypted_credentials_and_imports_existing_resources()
    {
        _connection.ServerProvider.Servers.Add(TestData.Remote("1"));

        var (id, report) = await CreateSut().ExecuteAsync(Command(" secret "), CancellationToken.None);

        var group = Assert.Single(_groups.Items);
        Assert.Equal(id, group.Id);
        Assert.Equal("enc:{\"api_token\":\"secret\"}", group.EncryptedCredentials); // trimmed, unknown fields dropped
        Assert.Equal(ServerOrigin.Imported, Assert.Single(_servers.Items).Origin);
        Assert.Equal(1, Assert.Single(report.Results).Added);
    }

    [Fact]
    public async Task Rejected_credentials_store_nothing()
    {
        _connection.RejectCredentials = true;

        await Assert.ThrowsAsync<ProviderException>(() => CreateSut().ExecuteAsync(Command(), CancellationToken.None));

        Assert.Empty(_groups.Items);
    }

    [Fact]
    public async Task Missing_credential_field_is_rejected() =>
        await Assert.ThrowsAsync<DomainException>(() => CreateSut().ExecuteAsync(Command(" "), CancellationToken.None));
}

public class GroupCredentialsTests
{
    [Fact]
    public void Round_trips_fields()
    {
        var json = GroupCredentials.Serialize(new Dictionary<string, string> { ["key"] = "a\"b", ["secret"] = "c" });

        var back = GroupCredentials.Deserialize(json);

        Assert.Equal("a\"b", back["key"]);
        Assert.Equal("c", back["secret"]);
    }

    [Fact]
    public void Bare_token_from_before_groups_is_read_as_api_token() =>
        Assert.Equal("hcloud-token", GroupCredentials.Deserialize("hcloud-token")[GroupCredentials.ApiTokenKey]);
}

public class DnsRecordNormalizationTests
{
    [Fact]
    public void Normalizes_name_type_and_values()
    {
        var set = DnsRecords.Normalize(new DnsRecordSet(" WWW. ", "a", 300, ["203.0.113.1", " ", "203.0.113.1", "203.0.113.2"]));

        Assert.Equal("www", set.Name);
        Assert.Equal("A", set.Type);
        Assert.Equal(["203.0.113.1", "203.0.113.2"], set.Values);
    }

    [Fact]
    public void Empty_name_means_apex() =>
        Assert.Equal("@", DnsRecords.Normalize(new DnsRecordSet("", "A", null, ["203.0.113.1"])).Name);

    [Fact]
    public void Txt_values_are_quoted_and_split_into_255_character_strings()
    {
        var longValue = new string('x', 300);

        var set = DnsRecords.Normalize(new DnsRecordSet("@", "TXT", null, ["v=spf1 -all", "\"already\"", longValue]));

        Assert.Equal("\"v=spf1 -all\"", set.Values[0]);
        Assert.Equal("\"already\"", set.Values[1]);
        Assert.Equal($"\"{new string('x', 255)}\" \"{new string('x', 45)}\"", set.Values[2]);
    }

    [Theory]
    [InlineData("www", "BOGUS", "1.2.3.4", null)]
    [InlineData("bad name", "A", "1.2.3.4", null)]
    [InlineData("www", "A", "", null)]
    [InlineData("www", "A", "1.2.3.4", 10)]
    public void Invalid_input_is_rejected(string name, string type, string value, int? ttl) =>
        Assert.Throws<DomainException>(() => DnsRecords.Normalize(new DnsRecordSet(name, type, ttl, [value])));

    [Fact]
    public void Cname_allows_only_one_value() =>
        Assert.Throws<DomainException>(() => DnsRecords.Normalize(new DnsRecordSet("www", "CNAME", null, ["a.", "b."])));

    [Theory]
    [InlineData("example.com")]
    [InlineData("Sub.Example.co.uk.")]
    public void Valid_zone_names_are_accepted(string name) =>
        Assert.Equal(name.ToLowerInvariant().TrimEnd('.'), Domain.Dns.DnsZone.NormalizeName(name));

    [Theory]
    [InlineData("localhost")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.com")]
    public void Invalid_zone_names_are_rejected(string name) =>
        Assert.Throws<DomainException>(() => Domain.Dns.DnsZone.NormalizeName(name));
}

public class ControlServerPowerTests
{
    [Fact]
    public async Task Shows_the_action_as_pending_and_requests_a_fast_sync()
    {
        var connection = new FakeConnection();
        var servers = new InMemoryServerRepository();
        var scheduler = new FakeSyncScheduler();
        var clock = new FakeClock();
        var server = Server.Import(Guid.NewGuid(), "9", "box", ObservedServerState.Running, null, "fsn1", "cx23", "x", 1m, "EUR", clock.UtcNow);
        servers.Add(server);
        var sut = new Application.Servers.ControlServerPower(
            new FakeConnectionFactory(connection), servers, scheduler, new FakeUnitOfWork(), clock);

        await sut.ExecuteAsync(server.Id, PowerAction.Shutdown, CancellationToken.None);

        Assert.Equal(("9", PowerAction.Shutdown), Assert.Single(connection.ServerProvider.PowerCalls));
        Assert.Equal(ServerStatus.Stopping, server.Status);
        Assert.Equal([server.GroupId], scheduler.Requested);
    }
}
