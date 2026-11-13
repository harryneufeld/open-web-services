using System.Buffers.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Infrastructure.Identity;
using OpenWebServices.Web.Services;

namespace OpenWebServices.Web.Auth;

/// <summary>
/// The signed-in user's own security: password, authenticator app, recovery codes, passkeys. Once an account has a
/// second factor, changing any of it needs a session that used one, so a stolen password alone cannot add an
/// attacker's passkey or switch 2FA off.
/// </summary>
public static class AccountEndpoints
{
    public const int RecoveryCodeCount = 10;
    private const string RecoveryCodesCookie = "ows.recovery-codes";
    private const string RecoveryCodesPurpose = "OpenWebServices.RecoveryCodesDisplay";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").RequireAuthorization().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        account.MapPost("/password", ChangePasswordAsync);
        account.MapPost("/authenticator/start", StartAuthenticatorAsync);
        account.MapPost("/authenticator/enable", EnableAuthenticatorAsync);
        account.MapPost("/authenticator/disable", DisableAuthenticatorAsync);
        account.MapPost("/recovery-codes", RenewRecoveryCodesAsync);
        account.MapPost("/passkeys/options", PasskeyCreationOptionsAsync);
        account.MapPost("/passkeys/add", AddPasskeyAsync);
        account.MapPost("/passkeys/remove", RemovePasskeyAsync);
        return app;
    }

    private static async Task<IResult> ChangePasswordAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var user = await RequireUserAsync(signIn, http.User);
        var result = await signIn.UserManager.ChangePasswordAsync(user, form["current"].ToString(), form["password"].ToString());
        if (!result.Succeeded)
            return Back(error: AuthEndpoints.Describe(result));

        await signIn.RefreshSignInAsync(user); // the security stamp changed; keep this session, end all others
        await AuthEndpoints.AuditAsync(http, AuditTypes.PasswordChanged, "", user.Id);
        return Back(status: "password");
    }

    /// <summary>Creates a fresh authenticator key to scan. Does not enable 2FA until a code proves the app has it.</summary>
    private static async Task<IResult> StartAuthenticatorAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var user = await RequireUserAsync(signIn, http.User);
        if (user.TwoFactorEnabled)
            return Back(error: "The authenticator app is already set up. Turn it off first to switch to a new one.");

        await signIn.UserManager.ResetAuthenticatorKeyAsync(user);
        await signIn.RefreshSignInAsync(user);
        return Back(anchor: "authenticator");
    }

    private static async Task<IResult> EnableAuthenticatorAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var users = signIn.UserManager;
        var user = await RequireUserAsync(signIn, http.User);
        if (user.TwoFactorEnabled)
            return Back(error: "The authenticator app is already set up.");

        var code = form["code"].ToString().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
            return Back(error: "That code is not valid. Check the time on your phone and try the current code.", anchor: "authenticator");

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) ?? [];
        StashRecoveryCodes(http, codes);

        // The code just proved possession of the second factor, so this session counts as strong from now on.
        await signIn.SignInWithClaimsAsync(user, isPersistent: false, [new Claim(Sessions.MethodClaim, "mfa")]);
        await AuthEndpoints.AuditAsync(http, AuditTypes.AuthenticatorEnabled, "", user.Id);
        return Results.LocalRedirect("/account/recovery-codes");
    }

    private static async Task<IResult> DisableAuthenticatorAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var users = signIn.UserManager;
        var user = await RequireUserAsync(signIn, http.User);
        if (!Sessions.IsStrong(http.User))
            return Back(error: StrongSessionRequired);
        if (user.IsOwsAdmin && (await users.GetPasskeysAsync(user)).Count == 0)
            return Back(error: "ows admins need a second factor. Add a passkey before turning the authenticator app off.");

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.RemoveAuthenticationTokenAsync(user, IdentityTokens.Provider, IdentityTokens.RecoveryCodes);
        await users.ResetAuthenticatorKeyAsync(user);
        await signIn.RefreshSignInAsync(user);
        await AuthEndpoints.AuditAsync(http, AuditTypes.AuthenticatorDisabled, "", user.Id);
        return Back(status: "authenticator-off");
    }

    private static async Task<IResult> RenewRecoveryCodesAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var user = await RequireUserAsync(signIn, http.User);
        if (!user.TwoFactorEnabled)
            return Back(error: "Recovery codes belong to the authenticator app. Set it up first.");
        if (!Sessions.IsStrong(http.User))
            return Back(error: StrongSessionRequired);

        StashRecoveryCodes(http, await signIn.UserManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) ?? []);
        await AuthEndpoints.AuditAsync(http, AuditTypes.RecoveryCodesRenewed, "", user.Id);
        return Results.LocalRedirect("/account/recovery-codes");
    }

    private static async Task<IResult> PasskeyCreationOptionsAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var user = await RequireUserAsync(signIn, http.User);
        if (!await MayChangeFactorsAsync(signIn.UserManager, user, http.User))
            return Results.Problem(StrongSessionRequired, statusCode: StatusCodes.Status403Forbidden);

        var name = user.UserName ?? user.Id.ToString();
        var options = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = user.Id.ToString(),
            Name = name,
            DisplayName = name,
        });
        return Results.Content(options, "application/json");
    }

    private static async Task<IResult> AddPasskeyAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var users = signIn.UserManager;
        var user = await RequireUserAsync(signIn, http.User);
        if (!await MayChangeFactorsAsync(users, user, http.User))
            return Back(error: StrongSessionRequired);

        var attestation = await signIn.PerformPasskeyAttestationAsync(form["credential"].ToString());
        if (!attestation.Succeeded)
            return Back(error: $"The passkey could not be added: {attestation.Failure.Message}", anchor: "passkeys");

        var passkey = attestation.Passkey;
        var label = form["name"].ToString().Trim();
        passkey.Name = label.Length is > 0 and <= 64 ? label : $"Passkey added {DateTime.UtcNow:yyyy-MM-dd}";
        var saved = await users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!saved.Succeeded)
            return Back(error: AuthEndpoints.Describe(saved), anchor: "passkeys");

        // Creating a passkey requires user verification on the authenticator: treat this session as strong.
        await signIn.SignInWithClaimsAsync(user, isPersistent: false, [new Claim(Sessions.MethodClaim, "passkey")]);
        await AuthEndpoints.AuditAsync(http, AuditTypes.PasskeyAdded, passkey.Name, user.Id);
        return Back(status: "passkey-added", anchor: "passkeys");
    }

    private static async Task<IResult> RemovePasskeyAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var users = signIn.UserManager;
        var user = await RequireUserAsync(signIn, http.User);
        if (!Sessions.IsStrong(http.User))
            return Back(error: StrongSessionRequired);

        byte[] credentialId;
        try { credentialId = Base64Url.DecodeFromChars(form["id"].ToString()); }
        catch (FormatException) { return Back(error: "Unknown passkey.", anchor: "passkeys"); }

        var passkeys = await users.GetPasskeysAsync(user);
        var passkey = passkeys.FirstOrDefault(p => p.CredentialId.AsSpan().SequenceEqual(credentialId));
        if (passkey is null)
            return Back(error: "Unknown passkey.", anchor: "passkeys");
        if (user.IsOwsAdmin && !user.TwoFactorEnabled && passkeys.Count == 1)
            return Back(error: "ows admins need a second factor. Set up the authenticator app or add another passkey first.", anchor: "passkeys");

        await users.RemovePasskeyAsync(user, credentialId);
        await signIn.RefreshSignInAsync(user);
        await AuthEndpoints.AuditAsync(http, AuditTypes.PasskeyRemoved, passkey.Name ?? "", user.Id);
        return Back(status: "passkey-removed", anchor: "passkeys");
    }

    /// <summary>Shows freshly generated recovery codes once: they travel in a short-lived encrypted cookie, never in a URL.</summary>
    public static IReadOnlyList<string>? TakeRecoveryCodes(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(RecoveryCodesCookie, out var value))
            return null;
        http.Response.Cookies.Delete(RecoveryCodesCookie, new CookieOptions { Path = "/account" });
        try
        {
            var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(RecoveryCodesPurpose, Sessions.UserId(http.User)?.ToString() ?? "").ToTimeLimitedDataProtector();
            return protector.Unprotect(value).Split(';');
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static void StashRecoveryCodes(HttpContext http, IEnumerable<string> codes)
    {
        var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(RecoveryCodesPurpose, Sessions.UserId(http.User)?.ToString() ?? "").ToTimeLimitedDataProtector();
        http.Response.Cookies.Append(RecoveryCodesCookie, protector.Protect(string.Join(';', codes), TimeSpan.FromMinutes(5)),
            new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/account", MaxAge = TimeSpan.FromMinutes(5) });
    }

    private const string StrongSessionRequired =
        "Sign out and sign in again with your authenticator code or a passkey to change your second factors.";

    /// <summary>The first second factor can be added from any session; after that only from a strong one.</summary>
    private static async Task<bool> MayChangeFactorsAsync(UserManager<OwsUser> users, OwsUser user, ClaimsPrincipal principal) =>
        Sessions.IsStrong(principal) || (!user.TwoFactorEnabled && (await users.GetPasskeysAsync(user)).Count == 0);

    private static async Task<OwsUser> RequireUserAsync(SignInManager<OwsUser> signIn, ClaimsPrincipal principal) =>
        await signIn.UserManager.GetUserAsync(principal) ?? throw new InvalidOperationException("The signed-in user no longer exists.");

    private static IResult Back(string? status = null, string? error = null, string? anchor = null)
    {
        var query = error is not null ? $"?error={Uri.EscapeDataString(error)}" : status is not null ? $"?status={status}" : "";
        return Results.LocalRedirect($"/account/security{query}{(anchor is null ? "" : "#" + anchor)}");
    }
}
