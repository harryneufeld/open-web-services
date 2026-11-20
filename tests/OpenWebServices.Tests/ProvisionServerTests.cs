using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Access;
using OpenWebServices.Application.Servers;
using OpenWebServices.Domain;
using OpenWebServices.Domain.SshKeys;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Tests;

public class ProvisionServerTests
{
    private static readonly ProviderGroup Group = TestData.Group();
    private static readonly Guid GroupId = Group.Id;

    private readonly FakeConnection _connection = new();
    private readonly InMemoryServerRepository _servers = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeSyncScheduler _scheduler = new();

    private FakeServerProvider Provider => _connection.ServerProvider;

    private readonly InMemoryGroupRepository _groups = new() { Items = { Group } };

    private readonly InMemorySshKeyRepository _keys = new();
    private readonly InMemoryGrantRepository _grants = new();
    private readonly AccessContext _access = TestData.Access(TenantRole.Member);

    private ProvisionServer CreateSut() => new(
        _access, _groups,
        new FakeConnectionFactory(_connection), _servers,
        new SshKeys(_access, _keys, _grants, new FakeUserDirectory(), new FakeAuditLog(), _unitOfWork, new FakeClock()), _grants,
        new FakeKeyGenerator(), new ReversingSecretProtector(), _scheduler, _unitOfWork, new FakeClock());

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
        Assert.Equal("enc:PRIVATE-1", server.EncryptedSshPrivateKey);
        Assert.Equal(ServerAccessWriter.AutomationLine(TestKeys.Ed25519Line(1)), server.AutomationPublicKey);
        Assert.Equal(ServerBootstrapScript.Build([server.AutomationPublicKey!]), Provider.LastRequest!.UserData);
        Assert.Equal([GroupId], _scheduler.Requested);
    }

    [Fact]
    public async Task Chosen_keys_get_access_through_cloud_init()
    {
        var mine = SshKey.Add(TestData.TenantId, _access.UserId, "laptop", SshPublicKey.Parse(TestKeys.Ed25519Line(50)), SshKeyOrigin.Pasted, _access.UserId, DateTime.UtcNow);
        _keys.Add(mine);

        await CreateSut().ExecuteAsync(Command() with { SshKeyIds = [mine.Id] }, CancellationToken.None);

        var grant = Assert.Single(_grants.Items);
        Assert.Equal(mine.Id, grant.SshKeyId);
        Assert.True(grant.IsPending); // confirmed by the first access check once the server runs
        Assert.Contains(mine.ToAuthorizedKeysLine(), Provider.LastRequest!.UserData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_members_keys_cannot_be_chosen()
    {
        var theirs = SshKey.Add(TestData.TenantId, Guid.NewGuid(), "theirs", SshPublicKey.Parse(TestKeys.Ed25519Line(51)), SshKeyOrigin.Pasted, null, DateTime.UtcNow);
        _keys.Add(theirs);

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().ExecuteAsync(Command() with { SshKeyIds = [theirs.Id] }, CancellationToken.None));
        Assert.Empty(_servers.Items);
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
