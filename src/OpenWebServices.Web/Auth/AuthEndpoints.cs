using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Admins;
using OpenWebServices.Domain;
using OpenWebServices.Web.Services;

namespace OpenWebServices.Web.Auth;

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/setup", SetupAsync);
        group.MapPost("/logout", LogoutAsync);
        return app;
    }

    private static async Task<IResult> LoginAsync(HttpContext http, UseCaseRunner runner, IFormCollection form)
    {
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var identity = await runner.RunAsync<AuthenticateAdmin, AdminIdentity?>(
            u => u.ExecuteAsync(form["username"].ToString(), form["password"].ToString(), http.RequestAborted));

        if (identity is null)
            return Results.LocalRedirect($"/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl)}");

        await SignInAsync(http, identity);
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> SetupAsync(HttpContext http, UseCaseRunner runner, IFormCollection form)
    {
        try
        {
            var identity = await runner.RunAsync<RegisterFirstAdmin, AdminIdentity>(
                u => u.ExecuteAsync(form["username"].ToString(), form["password"].ToString(), http.RequestAborted));
            await SignInAsync(http, identity);
            return Results.LocalRedirect("/");
        }
        catch (DomainException ex)
        {
            return Results.LocalRedirect($"/setup?error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static async Task LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        http.Response.Redirect("/login");
    }

    private static Task SignInAsync(HttpContext http, AdminIdentity admin)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim(ClaimTypes.Name, admin.Username),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    /// <summary>Only same-site relative paths are allowed, to prevent open redirects.</summary>
    private static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            ? url
            : "/";
}
