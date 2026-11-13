using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Platform;
using OpenWebServices.Application.Tenants;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Tenants;
using OpenWebServices.Infrastructure.Identity;
using OpenWebServices.Web.Services;

namespace OpenWebServices.Web.Auth;

/// <summary>Sign-in, first-run setup, invitations and tenant switching. Every form post is antiforgery-protected.</summary>
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/2fa", TwoFactorAsync);
        auth.MapPost("/passkey/options", PasskeyOptionsAsync);
        auth.MapPost("/passkey/login", PasskeyLoginAsync);
        auth.MapPost("/setup", SetupAsync);
        auth.MapPost("/logout", LogoutAsync);
        app.MapPost("/auth/switch-tenant", SwitchTenantAsync).RequireAuthorization();

        var invite = app.MapGroup("/invite").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        invite.MapPost("/accept", AcceptInvitationAsync).RequireAuthorization();
        invite.MapPost("/register", RegisterWithInvitationAsync);
        return app;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, SignInManager<OwsUser> signIn, IPasswordHasher<OwsUser> hasher, IFormCollection form)
    {
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var password = form["password"].ToString();
        var user = await signIn.UserManager.FindByNameAsync(form["username"].ToString().Trim());
        if (user is null)
        {
            hasher.HashPassword(new OwsUser(), password); // same cost as a real check: no user enumeration by timing
            await AuditAsync(http, AuditTypes.SignInFailed, $"Unknown user '{Truncate(form["username"])}'");
            return LoginError("1", returnUrl);
        }

        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            await AuditAsync(http, AuditTypes.SignIn, "Password", user.Id);
            return Results.LocalRedirect(returnUrl);
        }

        if (result.RequiresTwoFactor)
            return Results.LocalRedirect($"/login/2fa?returnUrl={Uri.EscapeDataString(returnUrl)}");

        await AuditAsync(http, result.IsLockedOut ? AuditTypes.LockedOut : AuditTypes.SignInFailed, "Password", user.Id);
        return LoginError(result.IsLockedOut ? "locked" : "1", returnUrl);
    }

    private static async Task<IResult> TwoFactorAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var useRecovery = form["mode"] == "recovery";
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
            return Results.LocalRedirect($"/login?error=expired&returnUrl={Uri.EscapeDataString(returnUrl)}");

        var code = form["code"].ToString().Replace(" ", "", StringComparison.Ordinal);
        var result = useRecovery
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(code)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code.Replace("-", "", StringComparison.Ordinal), isPersistent: false, rememberClient: false);

        if (result.Succeeded)
        {
            await AuditAsync(http, useRecovery ? AuditTypes.RecoveryCodeUsed : AuditTypes.SignIn,
                useRecovery ? "Signed in with a recovery code" : "Password and authenticator code", user.Id);
            return Results.LocalRedirect(returnUrl);
        }

        await AuditAsync(http, result.IsLockedOut ? AuditTypes.LockedOut : AuditTypes.SignInFailed,
            useRecovery ? "Wrong recovery code" : "Wrong authenticator code", user.Id);
        return result.IsLockedOut
            ? LoginError("locked", returnUrl)
            : Results.LocalRedirect($"/login/2fa?error=1&mode={(useRecovery ? "recovery" : "code")}&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    /// <summary>WebAuthn request options for any passkey of this site (discoverable credentials, no username).</summary>
    private static async Task<IResult> PasskeyOptionsAsync(SignInManager<OwsUser> signIn, IFormCollection form) =>
        Results.Content(await signIn.MakePasskeyRequestOptionsAsync(user: null), "application/json");

    private static async Task<IResult> PasskeyLoginAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var credential = form["credential"].ToString();
        if (string.IsNullOrEmpty(credential))
            return LoginError("passkey", returnUrl);

        var result = await signIn.PasskeySignInAsync(credential);
        if (result.Succeeded)
        {
            var user = await signIn.UserManager.GetUserAsync(signIn.Context.User);
            await AuditAsync(http, AuditTypes.SignIn, "Passkey", user?.Id);
            return Results.LocalRedirect(returnUrl);
        }

        await AuditAsync(http, result.IsLockedOut ? AuditTypes.LockedOut : AuditTypes.SignInFailed, "Passkey");
        return LoginError(result.IsLockedOut ? "locked" : "passkey", returnUrl);
    }

    /// <summary>Creates the first account (an ows admin) and its tenant. Closed for good once any account exists.</summary>
    private static async Task<IResult> SetupAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        await SetupLock.WaitAsync(http.RequestAborted);
        try
        {
            var users = signIn.UserManager;
            if (await users.Users.AnyAsync(http.RequestAborted))
                return SetupError("Setup has already been completed.");

            var tenantName = form["tenant"].ToString().Trim();
            if (tenantName.Length is 0 or > Tenant.MaxNameLength)
                return SetupError($"Organization name must be 1-{Tenant.MaxNameLength} characters.");

            var user = NewUser(form["username"], isOwsAdmin: true);
            var created = await users.CreateAsync(user, form["password"].ToString());
            if (!created.Succeeded)
                return SetupError(Describe(created));

            await http.RunAsync<CreateFirstTenant>(u => u.ExecuteAsync(user.Id, tenantName, Ip(http), http.RequestAborted));
            await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: "pwd");
            return Results.LocalRedirect("/account/security?status=welcome");
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SignInManager<OwsUser> signIn)
    {
        if (Sessions.UserId(http.User) is { } userId)
            await AuditAsync(http, AuditTypes.SignOut, "", userId);
        await signIn.SignOutAsync();
        return Results.LocalRedirect("/login");
    }

    private static async Task<IResult> SwitchTenantAsync(HttpContext http, IFormCollection form)
    {
        if (Guid.TryParse(form["tenantId"], out var tenantId))
            await http.RunAsync<SwitchTenant>(u => u.ExecuteAsync(tenantId, http.RequestAborted));
        return Results.LocalRedirect("/");
    }

    private static async Task<IResult> AcceptInvitationAsync(HttpContext http, IFormCollection form)
    {
        var token = form["token"].ToString();
        var userId = Sessions.UserId(http.User) ?? throw new InvalidOperationException("Not signed in.");
        try
        {
            await http.RunAsync<AcceptInvitation, Guid>(u => u.ExecuteAsync(token, userId, Ip(http), http.RequestAborted));
            return Results.LocalRedirect("/?joined=1");
        }
        catch (DomainException ex)
        {
            return InviteError(token, ex.Message);
        }
    }

    /// <summary>Creates an account for someone who opened an invitation link, joins the tenant and signs in.</summary>
    private static async Task<IResult> RegisterWithInvitationAsync(HttpContext http, SignInManager<OwsUser> signIn, IFormCollection form)
    {
        var token = form["token"].ToString();
        var preview = await http.RunAsync<InvitationQueries, InvitationPreview?>(q => q.DescribeAsync(token, http.RequestAborted));
        if (preview is not { IsUsable: true })
            return InviteError(token, "This invitation link has expired or was already used.");

        var user = NewUser(form["username"], isOwsAdmin: false);
        var created = await signIn.UserManager.CreateAsync(user, form["password"].ToString());
        if (!created.Succeeded)
            return InviteError(token, Describe(created));

        try
        {
            await http.RunAsync<AcceptInvitation, Guid>(u => u.ExecuteAsync(token, user.Id, Ip(http), http.RequestAborted));
        }
        catch (DomainException ex)
        {
            await signIn.UserManager.DeleteAsync(user); // someone else used the link meanwhile
            return InviteError(token, ex.Message);
        }

        await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: "pwd");
        // Admins and owners manage credentials: point them at second-factor setup first.
        return Results.LocalRedirect(preview.Role >= TenantRole.Admin ? "/account/security?status=welcome" : "/?joined=1");
    }

    private static OwsUser NewUser(string? userName, bool isOwsAdmin) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = (userName ?? "").Trim().ToLowerInvariant(),
        IsOwsAdmin = isOwsAdmin,
        LockoutEnabled = true,
        CreatedAtUtc = DateTime.UtcNow,
    };

    internal static async Task AuditAsync(HttpContext http, string type, string detail, Guid? userId = null)
    {
        await using var scope = http.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IAuditLog>().Add(new AuditEntry(type, detail, userId, null, Ip(http)));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
    }

    internal static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    internal static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));

    private static string Truncate(string? value) => value is { Length: > 64 } ? value[..64] : value ?? "";

    private static IResult LoginError(string code, string returnUrl) =>
        Results.LocalRedirect($"/login?error={code}&returnUrl={Uri.EscapeDataString(returnUrl)}");

    private static IResult SetupError(string message) =>
        Results.LocalRedirect($"/setup?error={Uri.EscapeDataString(message)}");

    private static IResult InviteError(string token, string message) =>
        Results.LocalRedirect($"/invite/{Uri.EscapeDataString(token)}?error={Uri.EscapeDataString(message)}");

    /// <summary>Only same-site relative paths are allowed, to prevent open redirects.</summary>
    internal static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            ? url
            : "/";
}
