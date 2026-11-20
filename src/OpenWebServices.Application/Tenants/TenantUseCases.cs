using System.Security.Cryptography;
using System.Text;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Tenants;

/// <summary>Invitation tokens: 256 random bits, URL-safe. Only the SHA-256 hash is stored.</summary>
public static class InvitationTokens
{
    public static (string Token, string Hash) Generate()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();
}

public sealed record MemberView(Guid UserId, string UserName, TenantRole Role, bool HasSecondFactor, DateTime JoinedAtUtc);

public sealed record InvitationView(Guid Id, TenantRole Role, string Note, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);

public sealed record TenantDetails(
    Tenant Tenant, TenantRole MyRole, IReadOnlyList<MemberView> Members, IReadOnlyList<InvitationView> Invitations);

public sealed class TenantQueries(
    AccessContext access,
    ITenantRepository tenants,
    ITenantMembershipRepository memberships,
    ITenantInvitationRepository invitations,
    IUserDirectory users,
    IClock clock)
{
    public async Task<TenantDetails> GetCurrentAsync(CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Viewer);
        var tenant = await tenants.FindAsync(tenantId, ct) ?? throw new NotFoundException("Tenant");
        var members = await memberships.ListByTenantAsync(tenantId, ct);
        var usersById = (await users.ListAsync([.. members.Select(m => m.UserId)], ct)).ToDictionary(u => u.Id);

        var memberViews = members
            .Where(m => usersById.ContainsKey(m.UserId))
            .Select(m => new MemberView(m.UserId, usersById[m.UserId].UserName, m.Role,
                usersById[m.UserId].HasSecondFactor, m.CreatedAtUtc))
            .OrderByDescending(m => m.Role).ThenBy(m => m.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<InvitationView> open = access.Can(TenantRole.Admin)
            ? [.. (await invitations.ListOpenAsync(tenantId, clock.UtcNow, ct))
                .Select(i => new InvitationView(i.Id, i.Role, i.Note, i.CreatedAtUtc, i.ExpiresAtUtc))]
            : [];

        return new TenantDetails(tenant, access.Role!.Value, memberViews, open);
    }
}

public sealed class UpdateTenantSettings(
    AccessContext access, ITenantRepository tenants, IAuditLog audit, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(string name, bool requireTwoFactor, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Owner);
        var tenant = await tenants.FindAsync(tenantId, ct) ?? throw new NotFoundException("Tenant");
        if (requireTwoFactor && !tenant.RequireTwoFactor && !access.StrongSession)
            throw new DomainException("Sign in with your authenticator app or a passkey before requiring a second factor, or you would lock yourself out.");

        tenant.Rename(name);
        tenant.SetRequireTwoFactor(requireTwoFactor);
        audit.Add(new AuditEntry(AuditTypes.TenantSettingsChanged,
            $"Name '{tenant.Name}', second factor {(requireTwoFactor ? "required" : "optional")}", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}

/// <summary>Creates a single-use invitation link for the current tenant. Only owners can invite owners.</summary>
public sealed class CreateInvitation(
    AccessContext access, ITenantInvitationRepository invitations, IAuditLog audit, IUnitOfWork unitOfWork, IClock clock)
{
    /// <returns>The token for the link. It is shown once and cannot be recovered later.</returns>
    public async Task<string> ExecuteAsync(TenantRole role, string note, CancellationToken ct)
    {
        var tenantId = access.Require(role == TenantRole.Owner ? TenantRole.Owner : TenantRole.Admin);
        var (token, hash) = InvitationTokens.Generate();
        invitations.Add(TenantInvitation.Create(tenantId, role, note, hash, access.UserId, clock.UtcNow));
        audit.Add(new AuditEntry(AuditTypes.InvitationCreated, $"{role} invitation{NoteSuffix(note)}", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
        return token;
    }

    internal static string NoteSuffix(string note) => string.IsNullOrWhiteSpace(note) ? "" : $" for '{note.Trim()}'";
}

public sealed class RevokeInvitation(
    AccessContext access, ITenantInvitationRepository invitations, IAuditLog audit, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task ExecuteAsync(Guid invitationId, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var invitation = await invitations.FindAsync(invitationId, ct);
        if (invitation is null || invitation.TenantId != tenantId)
            throw new NotFoundException("Invitation");
        if (invitation.Role == TenantRole.Owner)
            access.Require(TenantRole.Owner);

        invitation.Revoke(clock.UtcNow);
        audit.Add(new AuditEntry(AuditTypes.InvitationRevoked,
            $"{invitation.Role} invitation{CreateInvitation.NoteSuffix(invitation.Note)}", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}

public sealed record InvitationPreview(Guid TenantId, string TenantName, TenantRole Role, bool IsUsable);

/// <summary>Shows what an invitation link is for, before anyone signs in. The token is the authorization.</summary>
public sealed class InvitationQueries(ITenantInvitationRepository invitations, ITenantRepository tenants, IClock clock)
{
    public async Task<InvitationPreview?> DescribeAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)
            || await invitations.FindByTokenHashAsync(InvitationTokens.Hash(token), ct) is not { } invitation
            || await tenants.FindAsync(invitation.TenantId, ct) is not { } tenant)
            return null;
        return new InvitationPreview(tenant.Id, tenant.Name, invitation.Role, invitation.IsUsable(clock.UtcNow));
    }
}

/// <summary>
/// Joins a user to the invitation's tenant and selects it. The caller must pass the signed-in user (or the account
/// it just created for this invitation); the token itself is the authorization.
/// </summary>
public sealed class AcceptInvitation(
    ITenantInvitationRepository invitations,
    ITenantMembershipRepository memberships,
    IUserDirectory users,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Guid> ExecuteAsync(string token, Guid userId, string? ipAddress, CancellationToken ct)
    {
        var invitation = await invitations.FindByTokenHashAsync(InvitationTokens.Hash(token), ct)
                         ?? throw new DomainException("This invitation link is not valid.");
        invitation.Accept(userId, clock.UtcNow);

        var existing = await memberships.FindAsync(invitation.TenantId, userId, ct);
        if (existing is null)
            memberships.Add(TenantMembership.Create(invitation.TenantId, userId, invitation.Role, clock.UtcNow));
        else if (invitation.Role > existing.Role)
            existing.ChangeRole(invitation.Role); // an invitation never lowers an existing role

        audit.Add(new AuditEntry(AuditTypes.InvitationAccepted,
            $"Joined as {invitation.Role}{CreateInvitation.NoteSuffix(invitation.Note)}", userId, invitation.TenantId, ipAddress));
        await unitOfWork.SaveChangesAsync(ct);
        await users.SetCurrentTenantAsync(userId, invitation.TenantId, ct);
        return invitation.TenantId;
    }
}

public sealed class ChangeMemberRole(
    AccessContext access, ITenantMembershipRepository memberships, IUserDirectory users, IAuditLog audit, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid userId, TenantRole role, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var membership = await memberships.FindAsync(tenantId, userId, ct) ?? throw new NotFoundException("Member");
        if (membership.Role == role)
            return;
        if (membership.Role == TenantRole.Owner || role == TenantRole.Owner)
            access.Require(TenantRole.Owner);
        if (membership.Role == TenantRole.Owner)
            await MemberRules.EnsureAnotherOwnerAsync(memberships, tenantId, userId, ct);

        var before = membership.Role;
        membership.ChangeRole(role);
        var name = (await users.FindAsync(userId, ct))?.UserName ?? userId.ToString();
        audit.Add(new AuditEntry(AuditTypes.MemberRoleChanged, $"{name}: {before} → {role}", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}

/// <summary>Removes a member, or lets a member leave. A tenant always keeps at least one owner.</summary>
public sealed class RemoveMember(
    AccessContext access,
    ITenantMembershipRepository memberships,
    IUserDirectory users,
    ISshKeyRepository sshKeys,
    IServerKeyGrantRepository grants,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(Guid userId, CancellationToken ct)
    {
        var leaving = userId == access.UserId;
        var tenantId = access.Require(leaving ? TenantRole.Viewer : TenantRole.Admin);
        var membership = await memberships.FindAsync(tenantId, userId, ct) ?? throw new NotFoundException("Member");
        if (membership.Role == TenantRole.Owner)
        {
            if (!leaving)
                access.Require(TenantRole.Owner);
            await MemberRules.EnsureAnotherOwnerAsync(memberships, tenantId, userId, ct);
        }

        memberships.Remove(membership);
        var name = (await users.FindAsync(userId, ct))?.UserName ?? userId.ToString();
        audit.Add(new AuditEntry(AuditTypes.MemberRemoved, leaving ? $"{name} left" : $"{name} removed", access.UserId, tenantId));

        // Whoever leaves takes their server access with them: their keys are revoked and removed from every server.
        var revoked = await Access.SshKeys.RevokeAllOfAsync(sshKeys, grants, tenantId, userId, clock.UtcNow, ct);
        if (revoked > 0)
            audit.Add(new AuditEntry(SshAuditTypes.KeyRevoked, $"Revoked {revoked} SSH key(s) of {name}, who left the tenant", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}

internal static class MemberRules
{
    public static async Task EnsureAnotherOwnerAsync(
        ITenantMembershipRepository memberships, Guid tenantId, Guid userId, CancellationToken ct)
    {
        var owners = (await memberships.ListByTenantAsync(tenantId, ct)).Count(m => m.Role == TenantRole.Owner && m.UserId != userId);
        if (owners == 0)
            throw new DomainException("A tenant needs at least one owner. Make someone else owner first.");
    }
}
