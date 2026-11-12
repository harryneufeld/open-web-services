using OpenWebServices.Domain.Admins;

namespace OpenWebServices.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IAdminUserRepository
{
    void Add(AdminUser user);
    Task<AdminUser?> FindByUsernameAsync(string normalizedUsername, CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
}

public sealed record AdminIdentity(Guid Id, string Username);
