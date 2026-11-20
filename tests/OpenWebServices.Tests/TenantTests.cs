using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Platform;
using OpenWebServices.Application.Servers;
using OpenWebServices.Application.Tenants;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Tests;

internal sealed class InMemoryTenants : ITenantRepository, ITenantMembershipRepository, ITenantInvitationRepository
{
    public List<Tenant> Tenants { get; } = [];
    public List<TenantMembership> Memberships { get; } = [];
    public List<TenantInvitation> Invitations { get; } = [];

    public void Add(Tenant tenant) => Tenants.Add(tenant);
    Task<Tenant?> ITenantRepository.FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Tenants.FirstOrDefault(t => t.Id == id));
    public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Tenant>>(Tenants.ToList());
    public Task<bool> AnyAsync(CancellationToken ct) => Task.FromResult(Tenants.Count > 0);

    public void Add(TenantMembership membership) => Memberships.Add(membership);
    public void Remove(TenantMembership membership) => Memberships.Remove(membership);
    public Task<TenantMembership?> FindAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        Task.FromResult(Memberships.FirstOrDefault(m => m.TenantId == tenantId && m.UserId == userId));
    public Task<IReadOnlyList<TenantMembership>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TenantMembership>>(Memberships.Where(m => m.TenantId == tenantId).ToList());
    public Task<IReadOnlyList<TenantMembership>> ListByUserAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TenantMembership>>(Memberships.Where(m => m.UserId == userId).ToList());
    public Task<IReadOnlyList<TenantMembership>> ListAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TenantMembership>>(Memberships.ToList());

    public void Add(TenantInvitation invitation) => Invitations.Add(invitation);
    Task<TenantInvitation?> ITenantInvitationRepository.FindAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Invitations.FirstOrDefault(i => i.Id == id));
    public Task<TenantInvitation?> FindByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        Task.FromResult(Invitations.FirstOrDefault(i => i.TokenHash == tokenHash));
    public Task<IReadOnlyList<TenantInvitation>> ListOpenAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TenantInvitation>>(Invitations.Where(i => i.TenantId == tenantId && i.IsUsable(nowUtc)).ToList());
}

internal sealed class FakeUserDirectory : IUserDirectory
{
    public List<UserSummary> Users { get; } = [];

    public UserSummary Add(string name, bool admin = false, bool secondFactor = false, bool locked = false, Guid? currentTenant = null)
    {
        var user = new UserSummary(Guid.NewGuid(), name, admin, secondFactor, 0, locked, currentTenant, DateTime.UtcNow);
        Users.Add(user);
        return user;
    }

    public Task<bool> AnyAsync(CancellationToken ct) => Task.FromResult(Users.Count > 0);
    public Task<UserSummary?> FindAsync(Guid userId, CancellationToken ct) => Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));
    public Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<UserSummary>>(Users.ToList());
    public Task<IReadOnlyList<UserSummary>> ListAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserSummary>>(Users.Where(u => userIds.Contains(u.Id)).ToList());

    public Task SetCurrentTenantAsync(Guid userId, Guid? tenantId, CancellationToken ct) =>
        Replace(userId, u => u with { CurrentTenantId = tenantId });

    public Task SetOwsAdminAsync(Guid userId, bool isOwsAdmin, CancellationToken ct) => Replace(userId, u => u with { IsOwsAdmin = isOwsAdmin });
    public Task SetLockedAsync(Guid userId, bool locked, CancellationToken ct) => Replace(userId, u => u with { IsLockedOut = locked });
    public Task ResetSecondFactorsAsync(Guid userId, CancellationToken ct) => Replace(userId, u => u with { HasAuthenticator = false, PasskeyCount = 0 });

    private Task Replace(Guid userId, Func<UserSummary, UserSummary> change)
    {
        var index = Users.FindIndex(u => u.Id == userId);
        Users[index] = change(Users[index]);
        return Task.CompletedTask;
    }
}

public class TenantMembershipTests
{
    private readonly InMemoryTenants _store = new();
    private readonly FakeUserDirectory _users = new();
    private readonly FakeAuditLog _audit = new();
    private readonly FakeClock _clock = new();
    private readonly Tenant _tenant;

    public TenantMembershipTests()
    {
        _tenant = Tenant.Create("Acme", _clock.UtcNow);
        _store.Tenants.Add(_tenant);
    }

    private (UserSummary User, AccessContext Access) Member(string name, TenantRole role)
    {
        var user = _users.Add(name);
        _store.Memberships.Add(TenantMembership.Create(_tenant.Id, user.Id, role, _clock.UtcNow));
        var access = new AccessContext();
        access.SetUser(user.Id, name, strongSession: true, isOwsAdmin: false);
        access.SetTenant(_tenant.Id, _tenant.Name, role);
        return (user, access);
    }

    private CreateInvitation Invite(AccessContext access) => new(access, _store, _audit, new FakeUnitOfWork(), _clock);

    private AcceptInvitation Accept() => new(_store, _store, _users, _audit, new FakeUnitOfWork(), _clock);

    [Fact]
    public async Task Admin_invites_members_but_only_owners_invite_owners()
    {
        var (_, admin) = Member("admin", TenantRole.Admin);

        var token = await Invite(admin).ExecuteAsync(TenantRole.Member, "bob", CancellationToken.None);

        Assert.NotEmpty(token);
        Assert.Equal(InvitationTokens.Hash(token), Assert.Single(_store.Invitations).TokenHash);
        Assert.DoesNotContain(token, _store.Invitations[0].TokenHash, StringComparison.Ordinal); // only the hash is stored
        await Assert.ThrowsAsync<ForbiddenException>(() => Invite(admin).ExecuteAsync(TenantRole.Owner, "", CancellationToken.None));
    }

    [Fact]
    public async Task Members_and_viewers_cannot_invite()
    {
        var (_, member) = Member("member", TenantRole.Member);

        await Assert.ThrowsAsync<ForbiddenException>(() => Invite(member).ExecuteAsync(TenantRole.Viewer, "", CancellationToken.None));
    }

    [Fact]
    public async Task Invitation_adds_the_member_once_and_selects_the_tenant()
    {
        var (_, owner) = Member("owner", TenantRole.Owner);
        var token = await Invite(owner).ExecuteAsync(TenantRole.Member, "bob", CancellationToken.None);
        var bob = _users.Add("bob");

        var tenantId = await Accept().ExecuteAsync(token, bob.Id, null, CancellationToken.None);

        Assert.Equal(_tenant.Id, tenantId);
        Assert.Equal(TenantRole.Member, (await _store.FindAsync(_tenant.Id, bob.Id, CancellationToken.None))!.Role);
        Assert.Equal(_tenant.Id, (await _users.FindAsync(bob.Id, CancellationToken.None))!.CurrentTenantId);
        var eve = _users.Add("eve");
        await Assert.ThrowsAsync<DomainException>(() => Accept().ExecuteAsync(token, eve.Id, null, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_invitations_are_refused()
    {
        var (_, owner) = Member("owner", TenantRole.Owner);
        var token = await Invite(owner).ExecuteAsync(TenantRole.Member, "", CancellationToken.None);
        _clock.UtcNow += TenantInvitation.Lifetime + TimeSpan.FromMinutes(1);

        await Assert.ThrowsAsync<DomainException>(() => Accept().ExecuteAsync(token, _users.Add("late").Id, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_invitation_never_lowers_an_existing_role()
    {
        var (owner, ownerAccess) = Member("owner", TenantRole.Owner);
        Member("second-owner", TenantRole.Owner);
        var token = await Invite(ownerAccess).ExecuteAsync(TenantRole.Viewer, "", CancellationToken.None);

        await Accept().ExecuteAsync(token, owner.Id, null, CancellationToken.None);

        Assert.Equal(TenantRole.Owner, (await _store.FindAsync(_tenant.Id, owner.Id, CancellationToken.None))!.Role);
    }

    [Fact]
    public async Task The_last_owner_can_neither_leave_nor_be_demoted()
    {
        var (owner, access) = Member("owner", TenantRole.Owner);

        await Assert.ThrowsAsync<DomainException>(() =>
            new RemoveMember(access, _store, _users, new InMemorySshKeyRepository(), new InMemoryGrantRepository(), _audit, new FakeUnitOfWork(), _clock).ExecuteAsync(owner.Id, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() =>
            new ChangeMemberRole(access, _store, _users, _audit, new FakeUnitOfWork()).ExecuteAsync(owner.Id, TenantRole.Admin, CancellationToken.None));
    }

    [Fact]
    public async Task Admins_cannot_change_or_remove_owners()
    {
        var (owner, _) = Member("owner", TenantRole.Owner);
        var (_, admin) = Member("admin", TenantRole.Admin);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new ChangeMemberRole(admin, _store, _users, _audit, new FakeUnitOfWork()).ExecuteAsync(owner.Id, TenantRole.Viewer, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new RemoveMember(admin, _store, _users, new InMemorySshKeyRepository(), new InMemoryGrantRepository(), _audit, new FakeUnitOfWork(), _clock).ExecuteAsync(owner.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Requiring_a_second_factor_needs_a_strong_session()
    {
        var (owner, _) = Member("owner", TenantRole.Owner);
        var weak = new AccessContext();
        weak.SetUser(owner.Id, "owner", strongSession: false, isOwsAdmin: false);
        weak.SetTenant(_tenant.Id, _tenant.Name, TenantRole.Owner);

        await Assert.ThrowsAsync<DomainException>(() =>
            new UpdateTenantSettings(weak, _store, _audit, new FakeUnitOfWork()).ExecuteAsync("Acme", true, CancellationToken.None));
        Assert.False(_tenant.RequireTwoFactor);
    }
}

public class ResolveAccessTests
{
    private readonly InMemoryTenants _store = new();
    private readonly FakeUserDirectory _users = new();
    private readonly AccessContext _access = new();

    private async Task ResolveAsync(UserSummary user, bool strong) =>
        await new ResolveAccess(_users, _store, _store, _access).ExecuteAsync(user.Id, strong, CancellationToken.None);

    private Tenant AddTenant(string name, UserSummary member, TenantRole role, bool requireTwoFactor = false)
    {
        var tenant = Tenant.Create(name, DateTime.UtcNow);
        tenant.SetRequireTwoFactor(requireTwoFactor);
        _store.Tenants.Add(tenant);
        _store.Memberships.Add(TenantMembership.Create(tenant.Id, member.Id, role, DateTime.UtcNow));
        return tenant;
    }

    [Fact]
    public async Task Selects_the_current_tenant_with_its_role()
    {
        var user = _users.Add("ann");
        AddTenant("First", user, TenantRole.Owner);
        var second = AddTenant("Second", user, TenantRole.Viewer);
        await _users.SetCurrentTenantAsync(user.Id, second.Id, CancellationToken.None);

        await ResolveAsync(user, strong: false);

        Assert.Equal(second.Id, _access.TenantId);
        Assert.Equal(TenantRole.Viewer, _access.Role);
    }

    [Fact]
    public async Task A_stale_current_tenant_falls_back_to_a_real_membership()
    {
        var user = _users.Add("ann", currentTenant: Guid.NewGuid());
        var tenant = AddTenant("Only", user, TenantRole.Member);

        await ResolveAsync(user, strong: false);

        Assert.Equal(tenant.Id, _access.TenantId);
    }

    [Fact]
    public async Task Admins_without_a_second_factor_session_get_nothing()
    {
        var admin = _users.Add("root", admin: true);
        AddTenant("Acme", admin, TenantRole.Owner);

        await ResolveAsync(admin, strong: false);

        Assert.Equal(AccessRestriction.TwoFactorRequired, _access.Restriction);
        Assert.False(_access.IsOwsAdmin);
        Assert.Null(_access.TenantId);
        Assert.Throws<ForbiddenException>(() => _access.RequireOwsAdmin());
    }

    [Fact]
    public async Task Admins_with_a_strong_session_are_admins()
    {
        var admin = _users.Add("root", admin: true, secondFactor: true);

        await ResolveAsync(admin, strong: true);

        Assert.Equal(admin.Id, _access.RequireOwsAdmin());
    }

    [Fact]
    public async Task Tenants_requiring_a_second_factor_are_locked_for_password_sessions()
    {
        var user = _users.Add("ann");
        AddTenant("Strict", user, TenantRole.Owner, requireTwoFactor: true);

        await ResolveAsync(user, strong: false);

        Assert.Equal(AccessRestriction.TenantRequiresTwoFactor, _access.Restriction);
        Assert.Null(_access.TenantId);
    }

    [Fact]
    public async Task Locked_accounts_get_no_access()
    {
        var user = _users.Add("ann", locked: true);
        AddTenant("Acme", user, TenantRole.Owner);

        await ResolveAsync(user, strong: true);

        Assert.Null(_access.UserId);
        Assert.Null(_access.TenantId);
    }
}

public class PermissionTests
{
    [Fact]
    public async Task Viewers_cannot_create_servers()
    {
        var viewer = TestData.Access(TenantRole.Viewer);
        var grants = new InMemoryGrantRepository();
        var keys = new OpenWebServices.Application.Access.SshKeys(viewer, new InMemorySshKeyRepository(), grants,
            new FakeUserDirectory(), new FakeAuditLog(), new FakeUnitOfWork(), new FakeClock());
        var sut = new ProvisionServer(viewer, new InMemoryGroupRepository(),
            new FakeConnectionFactory(new FakeConnection()), new InMemoryServerRepository(), keys, grants, new FakeKeyGenerator(),
            new ReversingSecretProtector(), new FakeSyncScheduler(), new FakeUnitOfWork(), new FakeClock());

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.ExecuteAsync(new ProvisionServerCommand(Guid.NewGuid(), "web", "fsn1", "cx23", "ubuntu"), CancellationToken.None));
    }

    [Fact]
    public void Nobody_has_access_by_default()
    {
        var access = new AccessContext();

        Assert.False(access.Can(TenantRole.Viewer));
        Assert.Throws<ForbiddenException>(() => access.Require(TenantRole.Viewer));
        Assert.Throws<ForbiddenException>(() => access.RequireOwsAdmin());
    }

    [Fact]
    public async Task Only_admins_with_a_strong_session_create_tenants()
    {
        var store = new InMemoryTenants();
        var sut = new CreateTenant(TestData.Access(TenantRole.Owner), store, store, store, new FakeAuditLog(), new FakeUnitOfWork(), new FakeClock());

        await Assert.ThrowsAsync<ForbiddenException>(() => sut.ExecuteAsync("Evil", true, "", CancellationToken.None));
        Assert.Empty(store.Tenants);
    }
}
