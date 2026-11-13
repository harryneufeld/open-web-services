using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace OpenWebServices.Infrastructure.Identity;

/// <summary>
/// Identity's PBKDF2-HMAC-SHA512 hasher (210,000 iterations, the OWASP recommendation), plus verification of the
/// hashes from before Identity (<c>v1.iterations.salt.hash</c>, PBKDF2-SHA256). Those are upgraded on next sign-in.
/// </summary>
internal sealed class OwsPasswordHasher(IOptions<PasswordHasherOptions> options) : IPasswordHasher<OwsUser>
{
    public const int Iterations = 210_000;

    private readonly PasswordHasher<OwsUser> _current = new(options);

    public string HashPassword(OwsUser user, string password) => _current.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(OwsUser user, string hashedPassword, string providedPassword)
    {
        if (!hashedPassword.StartsWith("v1.", StringComparison.Ordinal))
            return _current.VerifyHashedPassword(user, hashedPassword, providedPassword);

        return VerifyLegacy(hashedPassword, providedPassword)
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Failed;
    }

    private static bool VerifyLegacy(string hash, string password)
    {
        var parts = hash.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations) || iterations < 100_000)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
