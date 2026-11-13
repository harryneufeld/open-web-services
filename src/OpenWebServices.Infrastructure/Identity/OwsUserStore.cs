using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using OpenWebServices.Infrastructure.Persistence;

namespace OpenWebServices.Infrastructure.Identity;

/// <summary>
/// The EF Core user store, with every stored authentication token (authenticator key, recovery codes) encrypted.
/// Identity keeps them in plain text by default, which would let a database dump generate valid 2FA codes.
/// </summary>
internal sealed class OwsUserStore(AppDbContext context, IDataProtectionProvider dataProtection, IdentityErrorDescriber? describer = null)
    : UserOnlyStore<OwsUser, AppDbContext, Guid>(context, describer)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("OpenWebServices.Identity.UserTokens.v1");

    public override Task SetTokenAsync(OwsUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(user, loginProvider, name, value is null ? null : For(user, loginProvider, name).Protect(value), cancellationToken);

    public override async Task<string?> GetTokenAsync(OwsUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var stored = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return stored is null ? null : For(user, loginProvider, name).Unprotect(stored);
    }

    // Bound to user and token name, so a value copied to another user or field does not decrypt.
    private IDataProtector For(OwsUser user, string loginProvider, string name) =>
        _protector.CreateProtector(user.Id.ToString(), loginProvider, name);
}
