using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Admins;

namespace OpenWebServices.Application.Admins;

/// <summary>True while no admin exists yet, i.e. the first-run setup page must be shown.</summary>
public sealed class IsSetupRequired(IAdminUserRepository admins)
{
    public async Task<bool> ExecuteAsync(CancellationToken ct) => !await admins.AnyAsync(ct);
}

/// <summary>Creates the very first admin. Refuses once any admin exists.</summary>
public sealed class RegisterFirstAdmin(
    IAdminUserRepository admins,
    IPasswordHasher hasher,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public const int MinPasswordLength = 12;

    public async Task<AdminIdentity> ExecuteAsync(string username, string password, CancellationToken ct)
    {
        if (await admins.AnyAsync(ct))
            throw new DomainException("Setup has already been completed.");
        if (password.Length < MinPasswordLength)
            throw new DomainException($"Password must be at least {MinPasswordLength} characters.");

        var user = AdminUser.Create(username, hasher.Hash(password), clock.UtcNow);
        admins.Add(user);
        await unitOfWork.SaveChangesAsync(ct);
        return new AdminIdentity(user.Id, user.Username);
    }
}

/// <summary>Checks credentials. Returns null for any failure, so callers cannot tell which part was wrong.</summary>
public sealed class AuthenticateAdmin(IAdminUserRepository admins, IPasswordHasher hasher)
{
    public async Task<AdminIdentity?> ExecuteAsync(string username, string password, CancellationToken ct)
    {
        var user = await admins.FindByUsernameAsync(AdminUser.Normalize(username), ct);
        if (user is null)
        {
            hasher.Hash(password); // spend comparable time so unknown users are not detectable by timing
            return null;
        }

        return hasher.Verify(password, user.PasswordHash) ? new AdminIdentity(user.Id, user.Username) : null;
    }
}
