using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Access;
using OpenWebServices.Infrastructure.Persistence;

namespace OpenWebServices.Infrastructure.Security;

/// <summary>
/// Servers created before ows recorded the public half of its key get it derived from the stored private key, so
/// they can be listed and managed like new ones. Runs at startup; does nothing once every server has it.
/// </summary>
internal static class AutomationKeyBackfill
{
    public static async Task<int> RunAsync(AppDbContext db, ISecretProtector secrets, ISshKeyGenerator generator, CancellationToken ct)
    {
        var servers = await db.Servers.IgnoreQueryFilters()
            .Where(s => s.EncryptedSshPrivateKey != null && s.AutomationPublicKey == null)
            .ToListAsync(ct);
        var filled = 0;
        foreach (var server in servers)
        {
            try
            {
                var privateKey = secrets.Unprotect(server.EncryptedSshPrivateKey!, server.TenantId, SecretPurposes.SshPrivateKey);
                server.SetAutomationPublicKey(ServerAccessWriter.AutomationLine(generator.PublicKeyOf(privateKey, $"ows-{server.Name}")));
                filled++;
            }
            catch (CryptographicException)
            {
                // The key ring cannot read it; the legacy upgrade already logged that.
            }
        }

        await db.SaveChangesAsync(ct);
        return filled;
    }
}
