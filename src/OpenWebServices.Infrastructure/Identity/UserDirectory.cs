using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Infrastructure.Persistence;

namespace OpenWebServices.Infrastructure.Identity;

internal sealed class UserDirectory(AppDbContext db, UserManager<OwsUser> users, TimeProvider time) : IUserDirectory
{
    public Task<bool> AnyAsync(CancellationToken ct) => db.Users.AnyAsync(ct);

    public async Task<UserSummary?> FindAsync(Guid userId, CancellationToken ct) =>
        (await QueryAsync(db.Users.Where(u => u.Id == userId), ct)).FirstOrDefault();

    public Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct) => QueryAsync(db.Users, ct);

    public Task<IReadOnlyList<UserSummary>> ListAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        QueryAsync(db.Users.Where(u => userIds.Contains(u.Id)), ct);

    public async Task SetCurrentTenantAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        var user = await RequireAsync(userId);
        user.CurrentTenantId = tenantId;
        Check(await users.UpdateAsync(user));
    }

    public async Task SetOwsAdminAsync(Guid userId, bool isOwsAdmin, CancellationToken ct)
    {
        var user = await RequireAsync(userId);
        user.IsOwsAdmin = isOwsAdmin;
        Check(await users.UpdateAsync(user));
        Check(await users.UpdateSecurityStampAsync(user));
    }

    public async Task SetLockedAsync(Guid userId, bool locked, CancellationToken ct)
    {
        var user = await RequireAsync(userId);
        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null));
        Check(await users.UpdateSecurityStampAsync(user)); // ends open sessions
    }

    public async Task ResetSecondFactorsAsync(Guid userId, CancellationToken ct)
    {
        var user = await RequireAsync(userId);
        foreach (var passkey in await users.GetPasskeysAsync(user))
            Check(await users.RemovePasskeyAsync(user, passkey.CredentialId));
        Check(await users.SetTwoFactorEnabledAsync(user, false));
        Check(await users.ResetAuthenticatorKeyAsync(user));
        Check(await users.RemoveAuthenticationTokenAsync(user, IdentityTokens.Provider, IdentityTokens.RecoveryCodes));
        Check(await users.UpdateSecurityStampAsync(user));
    }

    private async Task<IReadOnlyList<UserSummary>> QueryAsync(IQueryable<OwsUser> query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await query
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.IsOwsAdmin,
                u.TwoFactorEnabled,
                Passkeys = db.UserPasskeys.Count(p => p.UserId == u.Id),
                u.LockoutEnd,
                u.CurrentTenantId,
                u.CreatedAtUtc,
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new UserSummary(r.Id, r.UserName ?? "", r.IsOwsAdmin, r.TwoFactorEnabled, r.Passkeys,
            r.LockoutEnd is { } end && end > now, r.CurrentTenantId, r.CreatedAtUtc))];
    }

    private async Task<OwsUser> RequireAsync(Guid userId) =>
        await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User");

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}

/// <summary>Names Identity uses for the tokens it stores per user.</summary>
public static class IdentityTokens
{
    public const string Provider = "[AspNetUserStore]";
    public const string RecoveryCodes = "RecoveryCodes";
}
