using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Tests;

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class ReversingSecretProtector : ISecretProtector
{
    public string Protect(string plainText, Guid tenantId, string purpose) => "enc:" + plainText;
    public string Unprotect(string protectedText, Guid tenantId, string purpose) => protectedText["enc:".Length..];
}

internal sealed class FakeAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];
    public void Add(AuditEntry entry) => Entries.Add(entry);
    public Task<IReadOnlyList<AuditRecord>> ListRecentAsync(int count, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>([]);
}

internal sealed class FakeKeyGenerator : ISshKeyGenerator
{
    public SshKeyPair Generate(string comment) => new("PRIVATE", $"ssh-rsa AAAA {comment}");
}

internal sealed class FakeSyncScheduler : ISyncScheduler
{
    public List<Guid> Requested { get; } = [];
    public void RequestSync(Guid groupId) => Requested.Add(groupId);
}

internal sealed class InMemoryServerRepository : IServerRepository
{
    public List<Server> Items { get; } = [];

    public void Add(Server server) => Items.Add(server);
    public void Remove(Server server) => Items.Remove(server);
    public Task<Server?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(s => s.Id == id));
    public Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Server>>(Items.ToList());

    public Task<IReadOnlyList<Server>> ListByGroupAsync(Guid groupId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Server>>(Items.Where(s => s.GroupId == groupId).ToList());

    public Task<IReadOnlyList<Guid>> ListGroupsWithTransitioningServersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Items.Where(s => s.IsTransitioning).Select(s => s.GroupId).Distinct().ToList());
}

internal sealed class InMemoryDnsZoneRepository : IDnsZoneRepository
{
    public List<DnsZone> Items { get; } = [];

    public void Add(DnsZone zone) => Items.Add(zone);
    public void Remove(DnsZone zone) => Items.Remove(zone);
    public Task<DnsZone?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(z => z.Id == id));
    public Task<IReadOnlyList<DnsZone>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DnsZone>>(Items.ToList());

    public Task<IReadOnlyList<DnsZone>> ListByGroupAsync(Guid groupId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DnsZone>>(Items.Where(z => z.GroupId == groupId).ToList());
}

internal sealed class InMemoryGroupRepository : IProviderGroupRepository
{
    public List<ProviderGroup> Items { get; } = [];

    public void Add(ProviderGroup group) => Items.Add(group);
    public void Remove(ProviderGroup group) => Items.Remove(group);
    public Task<ProviderGroup?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(g => g.Id == id));
    public Task<IReadOnlyList<ProviderGroup>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderGroup>>(Items.ToList());
}

internal sealed class FakeServerProvider : IServerProvider
{
    public static readonly ServerOffer CheapOffer =
        new("fake", "fsn1", "Falkenstein", "DE", "cx23", 2, "shared", "x86", 4, 40, 4.00m, 4.76m, "EUR", true);

    public List<ServerOffer> Offers { get; } = [CheapOffer];
    public List<ProviderServer> Servers { get; } = [];
    public bool FailOnCreate { get; set; }
    public List<string> DeletedKeys { get; } = [];
    public List<(string Id, PowerAction Action)> PowerCalls { get; } = [];
    public CreateServerRequest? LastRequest { get; private set; }

    public Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ServerOffer>>(Offers);

    public Task<IReadOnlyList<ProviderServer>> ListServersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderServer>>(Servers.ToList());

    public Task<ProviderServer?> GetServerAsync(string externalId, CancellationToken ct) =>
        Task.FromResult(Servers.FirstOrDefault(s => s.ExternalId == externalId));

    public Task<string> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct) => Task.FromResult("7");

    public Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct)
    {
        LastRequest = request;
        return FailOnCreate
            ? throw new ProviderException("resource_unavailable")
            : Task.FromResult(new CreatedServer("42", "203.0.113.10"));
    }

    public Task PowerAsync(string externalId, PowerAction action, CancellationToken ct)
    {
        PowerCalls.Add((externalId, action));
        return Task.CompletedTask;
    }

    public Task DeleteServerAsync(string externalId, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteSshKeyAsync(string externalSshKeyId, CancellationToken ct)
    {
        DeletedKeys.Add(externalSshKeyId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDnsProvider : IDnsProvider
{
    public List<ProviderZone> Zones { get; } = [];
    public bool FailOnList { get; set; }

    public Task<IReadOnlyList<ProviderZone>> ListZonesAsync(CancellationToken ct) =>
        FailOnList ? throw new ProviderException("dns down") : Task.FromResult<IReadOnlyList<ProviderZone>>(Zones.ToList());

    public Task<ProviderZone> CreateZoneAsync(string name, CancellationToken ct)
    {
        var zone = new ProviderZone((Zones.Count + 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
            name, "ok", 3600, 2, ["ns1.example.net"], "unknown");
        Zones.Add(zone);
        return Task.FromResult(zone);
    }

    public Task DeleteZoneAsync(string zoneExternalId, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<DnsRecordSet>> ListRecordSetsAsync(string zoneExternalId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DnsRecordSet>>([]);
    public Task CreateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct) => Task.CompletedTask;
    public Task UpdateRecordSetAsync(string zoneExternalId, DnsRecordSet recordSet, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteRecordSetAsync(string zoneExternalId, string name, string type, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeConnection : IProviderConnection
{
    public static readonly ProviderTypeInfo FakeType = new(
        "fake", "Fake Cloud", "for tests", ResourceKind.Servers | ResourceKind.Dns,
        [new CredentialField("api_token", "API token")]);

    public ProviderTypeInfo Type => FakeType;
    public FakeServerProvider ServerProvider { get; } = new();
    public FakeDnsProvider DnsProvider { get; } = new();
    public IServerProvider Servers => ServerProvider;
    public IDnsProvider Dns => DnsProvider;
    public bool RejectCredentials { get; set; }

    public Task VerifyAsync(CancellationToken ct) =>
        RejectCredentials ? throw new ProviderException("token rejected") : Task.CompletedTask;
}

internal sealed class FakeConnectionFactory(FakeConnection connection) : IProviderConnectionFactory, IProviderCatalog
{
    public List<IReadOnlyDictionary<string, string>> ConnectedWith { get; } = [];

    public IReadOnlyList<ProviderTypeInfo> All => [FakeConnection.FakeType];
    public ProviderTypeInfo? Find(string providerType) => providerType == "fake" ? FakeConnection.FakeType : null;

    public Task<IProviderConnection> ConnectAsync(Guid groupId, CancellationToken ct) =>
        Task.FromResult<IProviderConnection>(connection);

    public IProviderConnection Connect(string providerType, IReadOnlyDictionary<string, string> credentials)
    {
        ConnectedWith.Add(credentials);
        return connection;
    }
}

internal static class TestData
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>An access context for a user with the given role in <see cref="TenantId"/>.</summary>
    public static AccessContext Access(TenantRole role = TenantRole.Owner)
    {
        var access = new AccessContext();
        access.SetUser(Guid.NewGuid(), "tester", strongSession: true, isOwsAdmin: false);
        access.SetTenant(TenantId, "Test tenant", role);
        return access;
    }

    public static ProviderGroup Group(ResourceKind kinds = ResourceKind.Servers | ResourceKind.Dns) =>
        ProviderGroup.Create(TenantId, "Test", "fake", "enc:{\"api_token\":\"t\"}", kinds, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    public static ProviderServer Remote(string id, string name = "web", ObservedServerState state = ObservedServerState.Running) =>
        new(id, name, state, "203.0.113." + id, "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR");
}
