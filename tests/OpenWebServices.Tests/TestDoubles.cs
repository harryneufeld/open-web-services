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
    public Task<IReadOnlyList<AuditRecord>> ListRecentAsync(Guid tenantId, IReadOnlyCollection<string> types, int count, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>(
            [.. Entries.Where(e => e.TenantId == tenantId && types.Contains(e.Type)).Reverse().Take(count)
                .Select(e => new AuditRecord(DateTime.UtcNow, e.Type, e.Detail, e.ActorUserId, e.TenantId, e.IpAddress))]);
}

/// <summary>
/// Hands out numbered key pairs: the "private key" is "PRIVATE-n" and the public key a valid Ed25519 line whose
/// key bytes encode n, so tests can tell which private key belongs to which public key.
/// </summary>
internal sealed class FakeKeyGenerator : ISshKeyGenerator
{
    private int _count;

    public SshKeyPair Generate(string comment)
    {
        var n = ++_count;
        return new($"PRIVATE-{n}", TestKeys.Ed25519Line((byte)n, comment));
    }

    public string PublicKeyOf(string privateKeyPem, string comment) =>
        TestKeys.Ed25519Line(byte.Parse(privateKeyPem["PRIVATE-".Length..], System.Globalization.CultureInfo.InvariantCulture), comment);
}

internal static class TestKeys
{
    /// <summary>A syntactically valid Ed25519 public key line whose 32 key bytes are all <paramref name="fill"/>.</summary>
    public static string Ed25519Line(byte fill, string comment = "test")
    {
        var blob = new List<byte>();
        void Write(byte[] data)
        {
            blob.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            blob.AddRange(data);
        }
        Write("ssh-ed25519"u8.ToArray());
        Write(Enumerable.Repeat(fill, 32).ToArray());
        return $"ssh-ed25519 {Convert.ToBase64String(blob.ToArray())} {comment}".TrimEnd();
    }

    public static string Blob(string line) => OpenWebServices.Domain.SshKeys.SshPublicKey.FindInAuthorizedKeysLine(line)!.Blob;
}

internal sealed class InMemorySshKeyRepository : ISshKeyRepository
{
    public List<OpenWebServices.Domain.SshKeys.SshKey> Items { get; } = [];

    public void Add(OpenWebServices.Domain.SshKeys.SshKey key) => Items.Add(key);
    public Task<OpenWebServices.Domain.SshKeys.SshKey?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(k => k.Id == id));
    public Task<IReadOnlyList<OpenWebServices.Domain.SshKeys.SshKey>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OpenWebServices.Domain.SshKeys.SshKey>>(Items.ToList());
    public Task<IReadOnlyList<OpenWebServices.Domain.SshKeys.SshKey>> ListByOwnerAsync(Guid tenantId, Guid ownerUserId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OpenWebServices.Domain.SshKeys.SshKey>>(Items.Where(k => k.TenantId == tenantId && k.OwnerUserId == ownerUserId).ToList());
}

internal sealed class InMemoryGrantRepository : IServerKeyGrantRepository
{
    public List<OpenWebServices.Domain.SshKeys.ServerKeyGrant> Items { get; } = [];

    public void Add(OpenWebServices.Domain.SshKeys.ServerKeyGrant grant) => Items.Add(grant);
    public void Remove(OpenWebServices.Domain.SshKeys.ServerKeyGrant grant) => Items.Remove(grant);
    public Task<IReadOnlyList<OpenWebServices.Domain.SshKeys.ServerKeyGrant>> ListForServerAsync(Guid serverId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OpenWebServices.Domain.SshKeys.ServerKeyGrant>>(Items.Where(g => g.ServerId == serverId).ToList());
    public Task<IReadOnlyList<OpenWebServices.Domain.SshKeys.ServerKeyGrant>> ListForKeyAsync(Guid sshKeyId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OpenWebServices.Domain.SshKeys.ServerKeyGrant>>(Items.Where(g => g.SshKeyId == sshKeyId).ToList());
    public Task<IReadOnlyList<Guid>> ListServersWithPendingChangesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Items.Where(g => g.IsPending).Select(g => g.ServerId).Distinct().ToList());
}

/// <summary>
/// A server's sshd and authorized_keys, simulated: a private key "PRIVATE-n" signs in when the matching public key is
/// in the file. Understands the two commands ows sends (read, and write via base64).
/// </summary>
internal sealed class FakeRemoteShell : IRemoteShell
{
    public string AuthorizedKeys { get; set; } = "";
    public HostKey HostKey { get; set; } = new("ED25519", "SHA256:host-key-1");
    public bool Unreachable { get; set; }
    public bool FailWrites { get; set; }
    public List<string> Commands { get; } = [];
    public int Writes { get; private set; }

    public Task<HostKey> WaitUntilReadyAsync(RemoteTarget target, TimeSpan timeout, CancellationToken ct)
    {
        Authenticate(target);
        return Task.FromResult(HostKey);
    }

    /// <summary>
    /// Simulates cloud-init's root lock: root keys that carry a command= option only print a message naming this
    /// user and exit 142. That user signs in with the same key and may use sudo without a password.
    /// </summary>
    public string? RootLockUser { get; set; }

    public bool SudoNeedsPassword { get; set; }

    public Task<CommandResult> RunAsync(RemoteTarget target, string command, CancellationToken ct)
    {
        Authenticate(target);
        Commands.Add($"{target.User}: {command}");
        if (target.User == "root" && RootLockUser is { } lockUser && UsedKeyLine(target).Contains("command=", StringComparison.Ordinal))
            return Task.FromResult(new CommandResult(142, $"Please login as the user \"{lockUser}\" rather than the user \"root\".\n\n"));
        if (target.User != "root")
        {
            if (target.User != RootLockUser)
                throw new RemoteAccessException($"{target.Host} refused the ows key for {target.User}.");
            if (!command.Contains("sudo -n ", StringComparison.Ordinal))
                return Task.FromResult(new CommandResult(1, "Permission denied"));
            if (SudoNeedsPassword)
                return Task.FromResult(new CommandResult(1, "sudo: a password is required"));
            command = command.Replace("sudo -n ", "", StringComparison.Ordinal);
        }
        if (command.StartsWith("cat ", StringComparison.Ordinal))
            return Task.FromResult(new CommandResult(0, AuthorizedKeys));
        if (command.Contains("base64 -d", StringComparison.Ordinal))
        {
            if (FailWrites)
                return Task.FromResult(new CommandResult(1, "disk full"));
            var start = command.IndexOf("printf '%s' '", StringComparison.Ordinal) + "printf '%s' '".Length;
            var encoded = command[start..command.IndexOf('\'', start)];
            AuthorizedKeys = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            Writes++;
            return Task.FromResult(new CommandResult(0, ""));
        }
        return Task.FromResult(new CommandResult(0, ""));
    }

    public Task UploadTextAsync(RemoteTarget target, string remotePath, string content, CancellationToken ct) => Task.CompletedTask;

    public bool HasKey(string publicLine) =>
        AuthorizedKeys.Split('\n').Any(l => OpenWebServices.Domain.SshKeys.SshPublicKey.FindInAuthorizedKeysLine(l)?.Blob == TestKeys.Blob(publicLine));

    private string UsedKeyLine(RemoteTarget target)
    {
        var blob = TestKeys.Blob(TestKeys.Ed25519Line(KeyNumber(target)));
        return AuthorizedKeys.Split('\n').First(l => OpenWebServices.Domain.SshKeys.SshPublicKey.FindInAuthorizedKeysLine(l)?.Blob == blob);
    }

    private static byte KeyNumber(RemoteTarget target) =>
        byte.Parse(target.PrivateKeyPem["PRIVATE-".Length..], System.Globalization.CultureInfo.InvariantCulture);

    private void Authenticate(RemoteTarget target)
    {
        if (Unreachable)
            throw new RemoteAccessException($"Could not connect to {target.Host} on port 22: timed out");
        if (target.PinnedHostKey is { } pinned && pinned != HostKey.Fingerprint)
            throw new HostKeyMismatchException(target.Host, pinned, HostKey.Fingerprint);
        var n = byte.Parse(target.PrivateKeyPem["PRIVATE-".Length..], System.Globalization.CultureInfo.InvariantCulture);
        if (!HasKey(TestKeys.Ed25519Line(n)))
            throw new RemoteAccessException($"{target.Host} refused the ows key.");
    }
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

    public Task<IReadOnlyList<Guid>> ListServersNeedingAccessCheckAsync(DateTime checkedBeforeUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Items
            .Where(s => s.CanManageKeys && (s.AccessCheckedAtUtc is null
                                            || ((s.RetiredAutomationPublicKey is not null || s.AccessError is not null) && s.AccessCheckedAtUtc < checkedBeforeUtc)))
            .Select(s => s.Id).ToList());
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

    /// <summary>Servers that exist but are not in the list yet, as with a provider whose list lags behind.</summary>
    public List<string> HiddenFromList { get; } = [];

    public Task<IReadOnlyList<ProviderServer>> ListServersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderServer>>(Servers.Where(s => !HiddenFromList.Contains(s.ExternalId)).ToList());

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
        "fake", "Fake Cloud", "for tests", ResourceKind.Servers | ResourceKind.Dns | ResourceKind.Domains,
        [new CredentialField("api_token", "API token")]);

    public ProviderTypeInfo Type => FakeType;
    public FakeServerProvider ServerProvider { get; } = new();
    public FakeDnsProvider DnsProvider { get; } = new();
    public IServerProvider Servers => ServerProvider;
    public IDnsProvider Dns => DnsProvider;
    public FakeFirewallProvider? FirewallProvider { get; set; } = new();
    public FakeDomainRegistrar? Registrar { get; set; } = new();
    public IServerNetworkProvider? Networks => null;
    public IFirewallProvider? Firewalls => FirewallProvider;
    public IDomainRegistrar? Domains => Registrar;
    public FakeLabelProvider? LabelProvider { get; set; } = new();
    public IServerLabelProvider? ServerLabels => LabelProvider;
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

internal sealed class FakeFirewallProvider : IFirewallProvider
{
    public Dictionary<string, IReadOnlyList<FirewallRule>> Managed { get; } = [];
    public bool FailOnRemove { get; set; }

    public Task<ServerFirewall> GetServerFirewallAsync(string externalServerId, CancellationToken ct) =>
        Task.FromResult(new ServerFirewall(Managed.GetValueOrDefault(externalServerId), []));

    public Task SetServerFirewallAsync(string externalServerId, IReadOnlyList<FirewallRule> inboundRules, CancellationToken ct)
    {
        Managed[externalServerId] = inboundRules;
        return Task.CompletedTask;
    }

    public Task RemoveServerFirewallAsync(string externalServerId, CancellationToken ct)
    {
        if (FailOnRemove)
            throw new ProviderException("firewall api down");
        Managed.Remove(externalServerId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDomainRegistrar : IDomainRegistrar
{
    public HashSet<string> Taken { get; } = ["taken.com"];
    public List<IReadOnlyList<string>> Checked { get; } = [];
    public List<(string QuoteId, DomainOrderOptions Options)> Placed { get; } = [];

    public Task<IReadOnlyList<DomainAvailability>> CheckAsync(IReadOnlyList<string> domains, CancellationToken ct)
    {
        Checked.Add(domains);
        return Task.FromResult<IReadOnlyList<DomainAvailability>>(
            [.. domains.Select(d => new DomainAvailability(d, !Taken.Contains(d), false, Taken.Contains(d) ? null : 9.99m, 12.99m, "EUR"))]);
    }

    public Task<DomainQuote> QuoteAsync(string domain, bool ownerIsOfLegalAge, CancellationToken ct) =>
        Task.FromResult(new DomainQuote("cart-1", domain, [new DomainQuoteLine(domain, 9.99m)], 9.99m, 11.89m, "EUR", []));

    public Task<PlacedDomainOrder> PlaceOrderAsync(string quoteId, DomainOrderOptions options, CancellationToken ct)
    {
        Placed.Add((quoteId, options));
        return Task.FromResult(new PlacedDomainOrder("1234", "https://registrar.example/pay/1234", 11.89m, "EUR", options.PayWithDefaultMethod));
    }

    public List<(string Domain, bool EndAtExpiry)> Cancellations { get; } = [];

    public Task<IReadOnlyList<RegisteredDomain>> ListDomainsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RegisteredDomain>>([new RegisteredDomain("mine.com")]);

    public Task SetEndAtExpiryAsync(string domain, bool endAtExpiry, CancellationToken ct)
    {
        Cancellations.Add((domain, endAtExpiry));
        return Task.CompletedTask;
    }

    public List<(string Domain, string AuthCode)> TransferQuotes { get; } = [];

    public Task<DomainQuote> QuoteTransferAsync(string domain, string authCode, bool ownerIsOfLegalAge, CancellationToken ct)
    {
        TransferQuotes.Add((domain, authCode));
        return Task.FromResult(new DomainQuote("cart-t", domain, [new DomainQuoteLine($"Transfer {domain}", 8.99m)], 8.99m, 10.70m, "EUR", [], IsTransfer: true));
    }

    public List<(string Domain, bool Locked)> LockChanges { get; } = [];

    public Task SetTransferLockAsync(string domain, bool locked, CancellationToken ct)
    {
        LockChanges.Add((domain, locked));
        return Task.CompletedTask;
    }

    public const string AuthCode = "Xy7#pQ2!";

    public Task<string> GetAuthCodeAsync(string domain, CancellationToken ct) => Task.FromResult(AuthCode);
}

internal sealed class FakeLabelProvider : IServerLabelProvider
{
    public Dictionary<string, IReadOnlyDictionary<string, string>> Labels { get; } = [];

    public Task<IReadOnlyDictionary<string, string>> GetLabelsAsync(string externalServerId, CancellationToken ct) =>
        Task.FromResult(Labels.GetValueOrDefault(externalServerId) ?? new Dictionary<string, string>());

    public Task SetLabelsAsync(string externalServerId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        Labels[externalServerId] = labels;
        return Task.CompletedTask;
    }
}
