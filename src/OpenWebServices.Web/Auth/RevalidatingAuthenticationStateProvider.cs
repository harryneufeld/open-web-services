using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenWebServices.Infrastructure.Identity;

namespace OpenWebServices.Web.Auth;

/// <summary>
/// Re-checks open Blazor circuits every minute: a changed security stamp (new password, 2FA change, admin rights,
/// account locked) ends the circuit's authentication, like the cookie validator does for normal requests.
/// </summary>
internal sealed class RevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopes,
    IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwsUser>>();
        var user = await users.GetUserAsync(authenticationState.User);
        if (user is null || await users.IsLockedOutAsync(user))
            return false;

        var stamp = authenticationState.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return stamp == await users.GetSecurityStampAsync(user);
    }
}
