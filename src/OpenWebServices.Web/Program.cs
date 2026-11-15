using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenWebServices.Application;
using OpenWebServices.Infrastructure;
using OpenWebServices.Web.Auth;
using OpenWebServices.Web.Components;
using OpenWebServices.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthenticationStateProvider>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<UseCaseRunner>();
// The security headers send X-Frame-Options: DENY (and CSP frame-ancestors) for every response already.
builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);

// "Always" (default outside Development) needs HTTPS. Set Authentication:AllowInsecureCookie=true only for
// plain-HTTP use on a trusted LAN.
var cookieSecurity = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Authentication:AllowInsecureCookie")
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(cookies =>
    {
        cookies.ApplicationCookie!.Configure(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Cookie.Name = "ows.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = cookieSecurity;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });
        cookies.TwoFactorUserIdCookie!.Configure(options =>
        {
            options.Cookie.Name = "ows.2fa";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = cookieSecurity;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
    });

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    // Password, 2FA, admin rights or a lock end other sessions within a minute.
    options.ValidationInterval = TimeSpan.FromMinutes(1);
    // Keep how the session was established (password, authenticator, passkey) when the cookie is refreshed.
    options.OnRefreshingPrincipal = context =>
    {
        if (context.NewPrincipal?.Identities.FirstOrDefault() is { } identity && !identity.HasClaim(c => c.Type == Sessions.MethodClaim))
        {
            foreach (var claim in context.CurrentPrincipal?.FindAll(Sessions.MethodClaim) ?? [])
                identity.AddClaim(new System.Security.Claims.Claim(claim.Type, claim.Value));
        }

        return Task.CompletedTask;
    };
});

// Everything requires a signed-in user unless it is explicitly marked [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.Logger.LogInformation("Starting ows {Version}", AppVersion.Full);

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    await app.Services.MigrateDatabaseAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseSecurityHeaders();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
// Framing is forbidden by the security headers' CSP; Blazor's own frame-ancestors header would duplicate it.
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = null);

app.Run();

public partial class Program;
