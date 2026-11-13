using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Infrastructure.Persistence;

namespace OpenWebServices.Infrastructure.Security;

/// <summary>
/// Authenticated encryption (ASP.NET Data Protection: AES-256-CBC + HMAC-SHA256, rotating keys) with a sub-key per
/// tenant and purpose. Values carry a version prefix so the format can change without guessing.
/// </summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Prefix = "v2:";

    private readonly IDataProtector _root = provider.CreateProtector("OpenWebServices.Secrets.v2");

    public string Protect(string plainText, Guid tenantId, string purpose) =>
        Prefix + For(tenantId, purpose).Protect(plainText);

    public string Unprotect(string protectedText, Guid tenantId, string purpose)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("The secret is stored in an outdated format. Restart ows to upgrade it.");
        return For(tenantId, purpose).Unprotect(protectedText[Prefix.Length..]);
    }

    private IDataProtector For(Guid tenantId, string purpose) => _root.CreateProtector(tenantId.ToString("N"), purpose);
}

/// <summary>
/// Re-encrypts secrets written before tenants existed (one key for everything) into the tenant-bound format.
/// Runs at startup after migrations; does nothing once every secret is upgraded.
/// </summary>
internal static class LegacySecretUpgrade
{
    private const string LegacyPurpose = "OpenWebServices.Secrets.v1";

    /// <returns>How many secrets were upgraded, and which could not be read (e.g. the old key ring is gone).</returns>
    public static async Task<(int Upgraded, IReadOnlyList<string> Unreadable)> RunAsync(
        AppDbContext db, IDataProtectionProvider provider, ISecretProtector secrets, IClock clock, CancellationToken ct)
    {
        var legacy = provider.CreateProtector(LegacyPurpose);
        var upgraded = 0;
        var unreadable = new List<string>();

        var groups = await db.ProviderGroups.IgnoreQueryFilters()
            .Where(g => !g.EncryptedCredentials.StartsWith(DataProtectionSecretProtector.Prefix)).ToListAsync(ct);
        foreach (var group in groups)
        {
            if (TryUnprotect(legacy, group.EncryptedCredentials) is not { } plain)
            {
                unreadable.Add($"credentials of provider group '{group.Name}'"); // its sync reports it; re-enter them
                continue;
            }

            group.ReplaceCredentials(secrets.Protect(plain, group.TenantId, SecretPurposes.ProviderCredentials), clock.UtcNow);
            upgraded++;
        }

        var servers = await db.Servers.IgnoreQueryFilters()
            .Where(s => s.EncryptedSshPrivateKey != null && !s.EncryptedSshPrivateKey.StartsWith(DataProtectionSecretProtector.Prefix))
            .ToListAsync(ct);
        foreach (var server in servers)
        {
            if (TryUnprotect(legacy, server.EncryptedSshPrivateKey!) is not { } plain)
            {
                unreadable.Add($"SSH key of server '{server.Name}'");
                continue;
            }

            server.ReplaceSshPrivateKey(secrets.Protect(plain, server.TenantId, SecretPurposes.SshPrivateKey));
            upgraded++;
        }

        await db.SaveChangesAsync(ct);
        return (upgraded, unreadable);
    }

    private static string? TryUnprotect(IDataProtector protector, string value)
    {
        try { return protector.Unprotect(value); }
        catch (CryptographicException) { return null; }
    }
}
