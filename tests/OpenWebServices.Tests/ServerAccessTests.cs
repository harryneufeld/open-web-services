using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Access;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.SshKeys;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Tests;

public class ServerAccessTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeKeyGenerator _generator = new();
    private readonly FakeRemoteShell _shell = new();
    private readonly InMemoryServerRepository _servers = new();
    private readonly InMemorySshKeyRepository _keys = new();
    private readonly InMemoryGrantRepository _grants = new();
    private readonly FakeUserDirectory _users = new();
    private readonly FakeAuditLog _audit = new();
    private readonly FakeConnection _connection = new();
    private readonly ServerAccessLocks _locks = new();
    private readonly Server _server;
    private readonly string _owsKey;

    public ServerAccessTests()
    {
        var pair = _generator.Generate("ows-web-1"); // PRIVATE-1
        _owsKey = pair.PublicKeyOpenSsh;
        _server = Server.Provision(TestData.TenantId, Guid.NewGuid(), "web-1", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR",
            "enc:" + pair.PrivateKeyPem, ServerAccessWriter.AutomationLine(pair.PublicKeyOpenSsh), _clock.UtcNow);
        _server.AttachProviderResources("42", "7", "203.0.113.10");
        _server.ApplyObserved(ObservedServerState.Running, null, _clock.UtcNow);
        _servers.Add(_server);
        _shell.AuthorizedKeys = pair.PublicKeyOpenSsh + "\n"; // what the provider put there at creation
    }

    private (ServerAccess Access, SshKeys Keys, AccessContext Context) Sut(TenantRole role = TenantRole.Member, AccessContext? context = null)
    {
        context ??= TestData.Access(role);
        var secrets = new ReversingSecretProtector();
        var keys = new SshKeys(context, _keys, _grants, _users, _audit, new FakeUnitOfWork(), _clock);
        var writer = new ServerAccessWriter(_grants, _keys, _shell, secrets, _clock);
        var access = new ServerAccess(context, _servers, _grants, _keys, keys, writer, _locks, _shell, _generator, secrets,
            new FakeConnectionFactory(_connection), _users, _audit, new FakeUnitOfWork(), _clock);
        return (access, keys, context);
    }

    private SshKey AddKey(byte fill, Guid? owner, string name = "laptop")
    {
        var key = SshKey.Add(TestData.TenantId, owner, name, SshPublicKey.Parse(TestKeys.Ed25519Line(fill)), SshKeyOrigin.Pasted, owner, _clock.UtcNow);
        _keys.Add(key);
        return key;
    }

    [Fact]
    public async Task Granting_writes_the_key_and_pins_the_host_key()
    {
        var (sut, _, context) = Sut();
        var key = AddKey(2, context.UserId);

        await sut.GrantAsync(_server.Id, key.Id, CancellationToken.None);

        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(2)));
        Assert.True(_shell.HasKey(_owsKey));
        Assert.Contains(AuthorizedKeysFile.BeginMarker, _shell.AuthorizedKeys, StringComparison.Ordinal);
        Assert.False(Assert.Single(_grants.Items).IsPending);
        Assert.Equal("SHA256:host-key-1", _server.HostKeyFingerprint);
        Assert.Null(_server.AccessError);
    }

    [Fact]
    public async Task A_grant_for_a_stopped_server_waits_and_the_background_check_writes_it()
    {
        _server.ApplyObserved(ObservedServerState.Stopped, null, _clock.UtcNow);
        var (sut, _, context) = Sut();
        var key = AddKey(2, context.UserId);

        await sut.GrantAsync(_server.Id, key.Id, CancellationToken.None);
        Assert.True(Assert.Single(_grants.Items).IsPending);
        Assert.Equal(0, _shell.Writes);

        _server.ApplyObserved(ObservedServerState.Running, null, _clock.UtcNow);
        await sut.ApplyInBackgroundAsync(_server.Id, CancellationToken.None);

        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(2)));
        Assert.False(_grants.Items[0].IsPending);
    }

    [Fact]
    public async Task Members_cannot_hand_out_other_members_keys()
    {
        var (sut, _, _) = Sut();
        var someoneElses = AddKey(2, Guid.NewGuid());

        await Assert.ThrowsAsync<ForbiddenException>(() => sut.GrantAsync(_server.Id, someoneElses.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_a_key_removes_it_from_the_server()
    {
        var (sut, keys, context) = Sut();
        var key = AddKey(2, context.UserId);
        await sut.GrantAsync(_server.Id, key.Id, CancellationToken.None);

        await keys.RevokeAsync(key.Id, CancellationToken.None);
        await sut.ApplyInBackgroundAsync(_server.Id, CancellationToken.None);

        Assert.False(_shell.HasKey(TestKeys.Ed25519Line(2)));
        Assert.True(_shell.HasKey(_owsKey));
        Assert.Empty(_grants.Items);
    }

    [Fact]
    public async Task Revoked_keys_are_removed_even_where_ows_never_put_them()
    {
        var (sut, keys, context) = Sut();
        var key = AddKey(2, context.UserId);
        _shell.AuthorizedKeys += TestKeys.Ed25519Line(2, "copied by hand") + "\n";

        await keys.RevokeAsync(key.Id, CancellationToken.None);
        await sut.CheckAsync(_server.Id, CancellationToken.None);

        Assert.False(_shell.HasKey(TestKeys.Ed25519Line(2)));
    }

    [Fact]
    public async Task Revoking_a_key_that_was_never_granted_still_sweeps_every_managed_server()
    {
        var (sut, keys, context) = Sut();
        await sut.CheckAsync(_server.Id, CancellationToken.None); // a clean, recent check
        var key = AddKey(2, context.UserId);
        _shell.AuthorizedKeys += TestKeys.Ed25519Line(2, "copied by hand") + "\n";
        _servers.Keys = _keys.Items;
        Assert.DoesNotContain(_server.Id, await _servers.ListServersNeedingAccessCheckAsync(_clock.UtcNow.AddMinutes(-2), CancellationToken.None));

        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        await keys.RevokeAsync(key.Id, CancellationToken.None);
        var due = await _servers.ListServersNeedingAccessCheckAsync(_clock.UtcNow.AddMinutes(-2), CancellationToken.None);
        Assert.Contains(_server.Id, due);

        await sut.ApplyInBackgroundAsync(_server.Id, CancellationToken.None);
        Assert.False(_shell.HasKey(TestKeys.Ed25519Line(2)));
        Assert.DoesNotContain(_server.Id, await _servers.ListServersNeedingAccessCheckAsync(_clock.UtcNow.AddMinutes(-2), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_keys_are_listed_and_can_be_removed_or_adopted()
    {
        _shell.AuthorizedKeys += TestKeys.Ed25519Line(8, "mallory") + "\n" + TestKeys.Ed25519Line(9, "old-laptop") + "\n";
        var (member, _, _) = Sut();

        await member.CheckAsync(_server.Id, CancellationToken.None);
        var view = await member.GetAsync(_server.Id, CancellationToken.None);
        Assert.Equal(["mallory", "old-laptop"], view.UnknownKeys.Select(k => k.Comment));

        await member.RemoveUnknownAsync(_server.Id, view.UnknownKeys[0].Fingerprint, CancellationToken.None);
        Assert.False(_shell.HasKey(TestKeys.Ed25519Line(8)));

        var (admin, _, _) = Sut(TenantRole.Admin);
        await admin.AdoptUnknownAsync(_server.Id, view.UnknownKeys[1].Fingerprint, "old laptop", CancellationToken.None);

        var adopted = Assert.Single(_keys.Items);
        Assert.True(adopted.IsShared);
        Assert.Equal(SshKeyOrigin.Adopted, adopted.Origin);
        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(9)));
        Assert.Empty(_server.UnknownAuthorizedKeys);
        Assert.Contains(_audit.Entries, e => e.Type == SshAuditTypes.UnknownKeyRemoved);
    }

    [Fact]
    public async Task Adopting_needs_the_admin_role()
    {
        _shell.AuthorizedKeys += TestKeys.Ed25519Line(9, "old-laptop") + "\n";
        var (member, _, _) = Sut();
        await member.CheckAsync(_server.Id, CancellationToken.None);
        var fingerprint = (await member.GetAsync(_server.Id, CancellationToken.None)).UnknownKeys[0].Fingerprint;

        await Assert.ThrowsAsync<ForbiddenException>(() => member.AdoptUnknownAsync(_server.Id, fingerprint, "x", CancellationToken.None));
    }

    [Fact]
    public async Task Rotation_swaps_the_ows_key_and_drops_the_provider_copy()
    {
        var (sut, _, _) = Sut();

        await sut.RotateAutomationKeyAsync(_server.Id, CancellationToken.None);

        Assert.Equal("enc:PRIVATE-2", _server.EncryptedSshPrivateKey);
        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(2)));
        Assert.False(_shell.HasKey(_owsKey));
        Assert.Null(_server.RetiredAutomationPublicKey);
        Assert.Null(_server.ExternalSshKeyId);
        Assert.Equal(["7"], _connection.ServerProvider.DeletedKeys);
        Assert.Contains(_audit.Entries, e => e.Type == SshAuditTypes.AutomationKeyRotated);
    }

    [Fact]
    public async Task A_failed_rotation_keeps_the_old_key()
    {
        _shell.FailWrites = true;
        var (sut, _, _) = Sut();

        await Assert.ThrowsAsync<RemoteAccessException>(() => sut.RotateAutomationKeyAsync(_server.Id, CancellationToken.None));

        Assert.Equal("enc:PRIVATE-1", _server.EncryptedSshPrivateKey);
        Assert.True(_shell.HasKey(_owsKey));
        Assert.NotNull(_server.AccessError);
    }

    [Fact]
    public async Task Revoking_the_ows_key_needs_another_working_key()
    {
        var (admin, _, _) = Sut(TenantRole.Admin);

        var ex = await Assert.ThrowsAsync<DomainException>(() => admin.RevokeAutomationKeyAsync(_server.Id, CancellationToken.None));

        Assert.Contains("No other key", ex.Message, StringComparison.Ordinal);
        Assert.True(_server.HasAutomationKey);
    }

    [Fact]
    public async Task Revoking_the_ows_key_removes_it_and_ows_forgets_it()
    {
        var (admin, _, context) = Sut(TenantRole.Admin);
        var key = AddKey(5, context.UserId);
        await admin.GrantAsync(_server.Id, key.Id, CancellationToken.None);

        await admin.RevokeAutomationKeyAsync(_server.Id, CancellationToken.None);

        Assert.False(_shell.HasKey(_owsKey));
        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(5)));
        Assert.False(_server.HasAutomationKey);
        Assert.Null(_server.RetiredAutomationPublicKey);
        Assert.False(_server.CanInstallApps);
        Assert.Equal(["7"], _connection.ServerProvider.DeletedKeys);
    }

    [Fact]
    public async Task Members_cannot_revoke_the_ows_key()
    {
        var (member, _, _) = Sut();
        await Assert.ThrowsAsync<ForbiddenException>(() => member.RevokeAutomationKeyAsync(_server.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_restored_ows_key_works_once_someone_adds_it()
    {
        var (admin, _, context) = Sut(TenantRole.Admin);
        var key = AddKey(5, context.UserId);
        await admin.GrantAsync(_server.Id, key.Id, CancellationToken.None);
        await admin.RevokeAutomationKeyAsync(_server.Id, CancellationToken.None);

        var line = await admin.RestoreAutomationKeyAsync(_server.Id, CancellationToken.None);
        await admin.ApplyInBackgroundAsync(_server.Id, CancellationToken.None);
        Assert.NotNull(_server.AccessError); // not on the server yet

        _shell.AuthorizedKeys += line + "\n"; // the user adds it with their own key
        await admin.CheckAsync(_server.Id, CancellationToken.None);

        Assert.Null(_server.AccessError);
        Assert.True(_server.CanInstallApps);
        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(5)));
    }

    [Fact]
    public async Task A_changed_host_key_stops_ows_until_an_admin_trusts_it()
    {
        var (admin, _, _) = Sut(TenantRole.Admin);
        await admin.CheckAsync(_server.Id, CancellationToken.None);
        _shell.HostKey = new HostKey("ED25519", "SHA256:someone-else");

        await Assert.ThrowsAsync<HostKeyMismatchException>(() => admin.CheckAsync(_server.Id, CancellationToken.None));
        Assert.Contains("someone-else", _server.AccessError, StringComparison.Ordinal);
        Assert.Equal("SHA256:host-key-1", _server.HostKeyFingerprint);

        await admin.ForgetHostKeyAsync(_server.Id, CancellationToken.None);
        await admin.CheckAsync(_server.Id, CancellationToken.None);

        Assert.Equal("SHA256:someone-else", _server.HostKeyFingerprint);
        Assert.Null(_server.AccessError);
    }

    [Fact]
    public async Task A_root_lock_from_cloud_init_is_repaired_with_sudo_as_the_named_user()
    {
        _shell.RootLockUser = "ubuntu";
        _shell.AuthorizedKeys = "no-port-forwarding,no-agent-forwarding,no-X11-forwarding,command=\"echo 'Please login as the user \\\"ubuntu\\\" rather than the user \\\"root\\\".';echo;sleep 10;exit 142\" "
                                + _owsKey + "\n";
        var (sut, _, context) = Sut();
        var key = AddKey(2, context.UserId);

        await sut.GrantAsync(_server.Id, key.Id, CancellationToken.None);

        Assert.Null(_server.AccessError);
        Assert.DoesNotContain("command=", _shell.AuthorizedKeys, StringComparison.Ordinal);
        Assert.True(_shell.HasKey(_owsKey));
        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(2)));
        Assert.Contains(_shell.Commands, c => c.StartsWith("ubuntu: printf", StringComparison.Ordinal) && c.Contains("| sudo -n sh -c", StringComparison.Ordinal));

        // Root works directly from now on.
        _shell.Commands.Clear();
        await sut.CheckAsync(_server.Id, CancellationToken.None);
        Assert.All(_shell.Commands, c => Assert.StartsWith("root: ", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_root_lock_without_passwordless_sudo_explains_the_manual_fix()
    {
        _shell.RootLockUser = "debian";
        _shell.SudoNeedsPassword = true;
        _shell.AuthorizedKeys = "command=\"echo 'Please login as the user \\\"debian\\\" rather than the user \\\"root\\\".';exit 142\" " + _owsKey + "\n";
        var (sut, _, _) = Sut();

        var ex = await Assert.ThrowsAsync<RemoteAccessException>(() => sut.CheckAsync(_server.Id, CancellationToken.None));

        Assert.Contains("'debian'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sudo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("command=", _shell.AuthorizedKeys, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(142, "Please login as the user \"ubuntu\" rather than the user \"root\".\n", "ubuntu")]
    [InlineData(142, "Please login as the user \\\"debian\\\" rather than the user \\\"root\\\".", "debian")]
    [InlineData(0, "ssh-ed25519 AAAA… someone", null)]
    [InlineData(1, "cat: permission denied", null)]
    public void Recognizes_the_cloud_init_root_lock(int exitCode, string output, string? user)
    {
        Assert.Equal(user, ServerAccessWriter.RootLoginRedirect(new CommandResult(exitCode, output)));
    }

    [Fact]
    public async Task An_unreachable_server_records_the_failure_for_a_later_retry()
    {
        _shell.Unreachable = true;
        var (sut, _, _) = Sut();

        await sut.ApplyInBackgroundAsync(_server.Id, CancellationToken.None);

        Assert.NotNull(_server.AccessError);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        Assert.Contains(_server.Id, await _servers.ListServersNeedingAccessCheckAsync(_clock.UtcNow.AddMinutes(-2), CancellationToken.None));
    }

    [Fact]
    public async Task Background_check_only_touches_keys_of_the_servers_tenant()
    {
        var otherTenant = SshKey.Add(Guid.NewGuid(), null, "elsewhere", SshPublicKey.Parse(TestKeys.Ed25519Line(7)), SshKeyOrigin.Pasted, null, _clock.UtcNow);
        otherTenant.Revoke(_clock.UtcNow);
        _keys.Add(otherTenant);
        _shell.AuthorizedKeys += TestKeys.Ed25519Line(7, "mine") + "\n";
        var system = new AccessContext();
        system.BecomeSystem();
        var (sut, _, _) = Sut(context: system);

        await sut.ApplyInBackgroundAsync(_server.Id, CancellationToken.None);

        Assert.True(_shell.HasKey(TestKeys.Ed25519Line(7)));
    }
}

public class SshKeysTests
{
    private readonly FakeClock _clock = new();
    private readonly InMemorySshKeyRepository _keys = new();
    private readonly InMemoryGrantRepository _grants = new();
    private readonly FakeAuditLog _audit = new();

    private SshKeys Sut(AccessContext access) => new(access, _keys, _grants, new FakeUserDirectory(), _audit, new FakeUnitOfWork(), _clock);

    [Fact]
    public async Task Members_add_personal_keys_and_admins_shared_ones()
    {
        var member = TestData.Access(TenantRole.Member);

        await Sut(member).AddAsync("laptop", SshPublicKeyTests.Ed25519, SshKeyOrigin.Pasted, shared: false, CancellationToken.None);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            Sut(member).AddAsync("ci", SshPublicKeyTests.Rsa3072, SshKeyOrigin.Pasted, shared: true, CancellationToken.None));
        await Sut(TestData.Access(TenantRole.Admin)).AddAsync("ci", SshPublicKeyTests.Rsa3072, SshKeyOrigin.Pasted, shared: true, CancellationToken.None);

        Assert.Equal(member.UserId, _keys.Items[0].OwnerUserId);
        Assert.True(_keys.Items[1].IsShared);
        Assert.Equal(2, _audit.Entries.Count(e => e.Type == SshAuditTypes.KeyAdded));
    }

    [Fact]
    public async Task The_same_key_cannot_be_added_twice()
    {
        var sut = Sut(TestData.Access(TenantRole.Member));
        await sut.AddAsync("laptop", SshPublicKeyTests.Ed25519, SshKeyOrigin.Pasted, false, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            sut.AddAsync("again", SshPublicKeyTests.Ed25519.Replace("alice@laptop", "other", StringComparison.Ordinal), SshKeyOrigin.Pasted, false, CancellationToken.None));
        Assert.Contains("'laptop'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewers_cannot_add_keys()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            Sut(TestData.Access(TenantRole.Viewer)).AddAsync("laptop", SshPublicKeyTests.Ed25519, SshKeyOrigin.Pasted, false, CancellationToken.None));
    }

    [Fact]
    public async Task Only_owners_of_a_key_or_admins_revoke_it()
    {
        var alice = TestData.Access(TenantRole.Member);
        var id = await Sut(alice).AddAsync("laptop", SshPublicKeyTests.Ed25519, SshKeyOrigin.Pasted, false, CancellationToken.None);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut(TestData.Access(TenantRole.Member)).RevokeAsync(id, CancellationToken.None));
        await Sut(TestData.Access(TenantRole.Admin)).RevokeAsync(id, CancellationToken.None);

        Assert.True(_keys.Items[0].IsRevoked);
    }

    [Fact]
    public async Task A_revoked_key_can_be_added_again()
    {
        var sut = Sut(TestData.Access(TenantRole.Member));
        var id = await sut.AddAsync("laptop", SshPublicKeyTests.Ed25519, SshKeyOrigin.Pasted, false, CancellationToken.None);
        await sut.RevokeAsync(id, CancellationToken.None);

        await sut.AddAsync("laptop", SshPublicKeyTests.Ed25519, SshKeyOrigin.Pasted, false, CancellationToken.None);

        Assert.Equal(2, _keys.Items.Count);
    }

    [Fact]
    public async Task Leaving_members_lose_their_keys_on_every_server()
    {
        var userId = Guid.NewGuid();
        var key = SshKey.Add(TestData.TenantId, userId, "laptop", SshPublicKey.Parse(SshPublicKeyTests.Ed25519), SshKeyOrigin.Pasted, userId, _clock.UtcNow);
        _keys.Add(key);
        var grant = ServerKeyGrant.Grant(TestData.TenantId, Guid.NewGuid(), key.Id, userId, _clock.UtcNow);
        grant.MarkApplied(_clock.UtcNow);
        _grants.Add(grant);

        var revoked = await SshKeys.RevokeAllOfAsync(_keys, _grants, TestData.TenantId, userId, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(1, revoked);
        Assert.True(key.IsRevoked);
        Assert.Equal(GrantState.Absent, grant.State);
        Assert.True(grant.IsPending);
    }
}
