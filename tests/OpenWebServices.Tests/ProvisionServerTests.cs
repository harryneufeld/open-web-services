using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Servers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Tests;

public class ProvisionServerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private readonly FakeConnection _connection = new();
    private readonly InMemoryServerRepository _servers = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeSyncScheduler _scheduler = new();

    private FakeServerProvider Provider => _connection.ServerProvider;

    private ProvisionServer CreateSut() => new(
        new FakeConnectionFactory(_connection), _servers, new FakeKeyGenerator(),
        new ReversingSecretProtector(), _scheduler, _unitOfWork, new FakeClock());

    private static ProvisionServerCommand Command(string type = "cx23") =>
        new(GroupId, "web-1", "fsn1", type, "ubuntu-24.04");

    [Fact]
    public async Task Creates_server_with_price_from_offer_and_bootstrap_script()
    {
        var id = await CreateSut().ExecuteAsync(Command(), CancellationToken.None);

        var server = Assert.Single(_servers.Items);
        Assert.Equal(id, server.Id);
        Assert.Equal(GroupId, server.GroupId);
        Assert.Equal(ServerOrigin.Created, server.Origin);
        Assert.Equal("42", server.ExternalId);
        Assert.Equal("7", server.ExternalSshKeyId);
        Assert.Equal("203.0.113.10", server.PublicIpv4);
        Assert.Equal(4.00m, server.MonthlyPriceNet);
        Assert.Equal("enc:PRIVATE", server.EncryptedSshPrivateKey);
        Assert.Equal(ServerBootstrapScript.Content, Provider.LastRequest!.UserData);
        Assert.Equal([GroupId], _scheduler.Requested);
    }

    [Fact]
    public async Task Persists_the_server_before_calling_the_provider()
    {
        Provider.FailOnCreate = true;

        await CreateSut().ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(2, _unitOfWork.SaveCount); // once before the provider call, once after
    }

    [Fact]
    public async Task Provider_failure_marks_server_failed_and_removes_the_ssh_key()
    {
        Provider.FailOnCreate = true;

        await CreateSut().ExecuteAsync(Command(), CancellationToken.None);

        var server = Assert.Single(_servers.Items);
        Assert.Equal(ServerStatus.Failed, server.Status);
        Assert.Equal("resource_unavailable", server.FailureReason);
        Assert.Equal(["7"], Provider.DeletedKeys);
    }

    [Fact]
    public async Task Unknown_offer_is_rejected_without_creating_anything()
    {
        await Assert.ThrowsAsync<OfferUnavailableException>(
            () => CreateSut().ExecuteAsync(Command("cx999"), CancellationToken.None));

        Assert.Empty(_servers.Items);
    }

    [Fact]
    public async Task Unavailable_offer_is_rejected()
    {
        Provider.Offers[0] = FakeServerProvider.CheapOffer with { Available = false };

        await Assert.ThrowsAsync<OfferUnavailableException>(
            () => CreateSut().ExecuteAsync(Command(), CancellationToken.None));
    }
}
