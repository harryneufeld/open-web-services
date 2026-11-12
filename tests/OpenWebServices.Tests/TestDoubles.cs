using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;

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
    public string Protect(string plainText) => "enc:" + plainText;
    public string Unprotect(string protectedText) => protectedText["enc:".Length..];
}

internal sealed class FakeKeyGenerator : ISshKeyGenerator
{
    public SshKeyPair Generate(string comment) => new("PRIVATE", $"ssh-rsa AAAA {comment}");
}

internal sealed class InMemoryServerRepository : IServerRepository
{
    public List<Server> Items { get; } = [];

    public void Add(Server server) => Items.Add(server);
    public void Remove(Server server) => Items.Remove(server);
    public Task<Server?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(s => s.Id == id));
    public Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Server>>(Items);

    public Task<IReadOnlyList<Server>> ListByStatusAsync(ServerStatus status, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Server>>(Items.Where(s => s.Status == status).ToList());
}

internal sealed class FakeHostingProvider : IHostingProvider
{
    public static readonly ServerOffer CheapOffer =
        new("hetzner", "fsn1", "Falkenstein", "DE", "cx23", 2, "shared", "x86", 4, 40, 4.00m, 4.76m, "EUR", true);

    public string Id => "hetzner";
    public List<ServerOffer> Offers { get; } = [CheapOffer];
    public bool FailOnCreate { get; set; }
    public List<long> DeletedKeys { get; } = [];
    public CreateServerRequest? LastRequest { get; private set; }

    public Task<IReadOnlyList<ServerOffer>> GetOffersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ServerOffer>>(Offers);

    public Task<long> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct) => Task.FromResult(7L);

    public Task<CreatedServer> CreateServerAsync(CreateServerRequest request, CancellationToken ct)
    {
        LastRequest = request;
        return FailOnCreate
            ? throw new ProviderException("resource_unavailable")
            : Task.FromResult(new CreatedServer(42, "203.0.113.10"));
    }

    public Task<ProviderServerState?> GetServerAsync(long providerServerId, CancellationToken ct) =>
        Task.FromResult<ProviderServerState?>(new ProviderServerState(ProviderServerStatus.Running, "203.0.113.10"));

    public Task PowerAsync(long providerServerId, PowerAction action, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteServerAsync(long providerServerId, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteSshKeyAsync(long providerSshKeyId, CancellationToken ct)
    {
        DeletedKeys.Add(providerSshKeyId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeProviderFactory(FakeHostingProvider provider) : IHostingProviderFactory
{
    public IReadOnlyCollection<string> ProviderIds { get; } = ["hetzner"];
    public Task<IHostingProvider> GetAsync(string providerId, CancellationToken ct) => Task.FromResult<IHostingProvider>(provider);
}
