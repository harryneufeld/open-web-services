using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Tenants;
using OpenWebServices.Domain.Tenants;
using OpenWebServices.Infrastructure.Identity;
using OpenWebServices.Infrastructure.Persistence;

namespace OpenWebServices.Tests;

/// <summary>The real web app over an in-memory database: setup, sign-in, second factors, invitations.</summary>
public sealed partial class AuthIntegrationTests : IDisposable
{
    private const string StrongPassword = "correct horse battery";

    private readonly TestFactory _factory = new();
    private readonly HttpClient _client;

    public AuthIntegrationTests() => _client = NewClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Anonymous_request_to_a_protected_page_is_sent_to_login()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", RedirectTarget(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_page_sends_to_setup_until_an_account_exists()
    {
        var response = await _client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/setup", RedirectTarget(response));
    }

    [Fact]
    public async Task Stylesheet_is_served_to_anonymous_visitors()
    {
        var html = await (await _client.GetAsync("/setup")).Content.ReadAsStringAsync();
        var href = StylesheetHref().Match(html).Groups[1].Value;

        var response = await _client.GetAsync("/" + href.TrimStart('/'));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _client.GetAsync("/setup");

        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Setup_creates_an_admin_who_must_add_a_second_factor_first()
    {
        var setup = await SetupAsync();

        Assert.Equal("/account/security?status=welcome", RedirectTarget(setup));
        var home = await (await _client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("<h1>Second factor required</h1>", home, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Console home</h1>", home, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/", "Console home")]
    [InlineData("/servers", "Servers")]
    [InlineData("/servers/new", "New server")]
    [InlineData("/groups", "Provider groups")]
    [InlineData("/dns", "DNS zones")]
    [InlineData("/domains", "Domains")]
    [InlineData("/tenant", "Members")]
    [InlineData("/admin", "Platform admin")]
    [InlineData("/account/security", "Account security")]
    public async Task Pages_render_for_an_admin_with_an_authenticator(string path, string heading)
    {
        await SetupWithAuthenticatorAsync();

        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<h1>{heading}</h1>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabling_the_authenticator_shows_recovery_codes_once()
    {
        await SetupAsync();
        await PostFormAsync("/account/security", "/account/authenticator/start");
        var enable = await PostFormAsync("/account/security", "/account/authenticator/enable", ("code", Totp(await AuthenticatorKeyAsync())));

        Assert.Equal("/account/recovery-codes", RedirectTarget(enable));
        var first = await (await _client.GetAsync("/account/recovery-codes")).Content.ReadAsStringAsync();
        Assert.Equal(AccountTestCounts.RecoveryCodes, RecoveryCode().Matches(first).Count);
        var second = await (await _client.GetAsync("/account/recovery-codes")).Content.ReadAsStringAsync();
        Assert.Empty(RecoveryCode().Matches(second));
    }

    [Fact]
    public async Task A_wrong_authenticator_code_does_not_enable_2fa()
    {
        await SetupAsync();
        await PostFormAsync("/account/security", "/account/authenticator/start");

        var enable = await PostFormAsync("/account/security", "/account/authenticator/enable", ("code", "000000"));

        Assert.StartsWith("/account/security?error=", RedirectTarget(enable), StringComparison.Ordinal);
        Assert.Contains("Second factor required", await (await _client.GetAsync("/")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_with_2fa_needs_password_and_authenticator_code()
    {
        await SetupWithAuthenticatorAsync();
        using var visitor = NewClient();

        var login = await PostFormAsync("/login", "/auth/login", visitor, ("username", "Admin"), ("password", StrongPassword));
        Assert.StartsWith("/login/2fa", RedirectTarget(login), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/")).StatusCode); // not signed in yet

        var wrong = await PostFormAsync("/login/2fa", "/auth/2fa", visitor, ("code", "000000"));
        Assert.StartsWith("/login/2fa?error=1", RedirectTarget(wrong), StringComparison.Ordinal);

        var verified = await PostFormAsync("/login/2fa", "/auth/2fa", visitor, ("code", Totp(await AuthenticatorKeyAsync())));
        Assert.Equal("/", RedirectTarget(verified));
        Assert.Contains("<h1>Console home</h1>", await (await visitor.GetAsync("/")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_rejects_short_passwords()
    {
        var response = await SetupAsync(password: "short");

        Assert.StartsWith("/setup?error=", RedirectTarget(response), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await _client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Setup_cannot_be_repeated_once_an_account_exists()
    {
        await SetupAsync();
        using var attacker = NewClient();

        // The setup page is hidden once an account exists, so a direct POST borrows a token from the login page.
        var response = await PostFormAsync("/login", "/auth/setup", attacker,
            ("tenant", "Evil"), ("username", "evil"), ("password", StrongPassword));

        Assert.StartsWith("/setup?error=", RedirectTarget(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_locks_after_five_attempts()
    {
        await SetupAsync();
        using var visitor = NewClient();

        for (var i = 0; i < 4; i++)
        {
            var login = await PostFormAsync("/login", "/auth/login", visitor, ("username", "admin"), ("password", "wrong-password!"));
            Assert.StartsWith("/login?error=1", RedirectTarget(login), StringComparison.Ordinal);
        }

        var fifth = await PostFormAsync("/login", "/auth/login", visitor, ("username", "admin"), ("password", "wrong-password!"));
        Assert.StartsWith("/login?error=locked", RedirectTarget(fifth), StringComparison.Ordinal);
        var correct = await PostFormAsync("/login", "/auth/login", visitor, ("username", "admin"), ("password", StrongPassword));
        Assert.StartsWith("/login?error=locked", RedirectTarget(correct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_signs_out()
    {
        await SetupAsync();

        await PostFormAsync("/account/security", "/auth/logout");

        Assert.Equal(HttpStatusCode.Redirect, (await _client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    public async Task Login_ignores_external_return_urls(string returnUrl)
    {
        await SetupAsync();
        using var visitor = NewClient();

        var login = await PostFormAsync("/login", "/auth/login", visitor,
            ("username", "admin"), ("password", StrongPassword), ("returnUrl", returnUrl));

        Assert.Equal("/", RedirectTarget(login));
    }

    [Fact]
    public async Task Posting_without_an_antiforgery_token_is_refused()
    {
        var response = await _client.PostAsync("/auth/setup", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["tenant"] = "Acme", ["username"] = "admin", ["password"] = StrongPassword }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_invitation_link_creates_a_member_once()
    {
        await SetupAsync();
        var token = await CreateInvitationAsync(TenantRole.Member);
        using var invited = NewClient();

        var page = await (await invited.GetAsync($"/invite/{token}")).Content.ReadAsStringAsync();
        Assert.Contains("<h1>Join Acme</h1>", page, StringComparison.Ordinal);

        var register = await PostFormAsync($"/invite/{token}", "/invite/register", invited,
            ("token", token), ("username", "bob"), ("password", StrongPassword));
        Assert.Equal("/?joined=1", RedirectTarget(register));
        Assert.Contains("<h1>Servers</h1>", await (await invited.GetAsync("/servers")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var second = NewClient();
        var reuse = await (await second.GetAsync($"/invite/{token}")).Content.ReadAsStringAsync();
        Assert.Contains("Invitation not valid", reuse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_do_not_see_platform_admin_data()
    {
        await SetupAsync();
        var token = await CreateInvitationAsync(TenantRole.Owner);
        using var invited = NewClient();
        await PostFormAsync($"/invite/{token}", "/invite/register", invited, ("token", token), ("username", "bob"), ("password", StrongPassword));

        var admin = await (await invited.GetAsync("/admin")).Content.ReadAsStringAsync();

        // The page prerenders its frame only; the query refusal itself is covered in TenantTests.
        Assert.Contains("<h1>Platform admin</h1>", admin, StringComparison.Ordinal);
        Assert.DoesNotContain(">admin <", admin, StringComparison.Ordinal);
    }

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private Task<HttpResponseMessage> SetupAsync(string password = StrongPassword) =>
        PostFormAsync("/setup", "/auth/setup", ("tenant", "Acme"), ("username", "admin"), ("password", password));

    private async Task SetupWithAuthenticatorAsync()
    {
        await SetupAsync();
        await PostFormAsync("/account/security", "/account/authenticator/start");
        var enable = await PostFormAsync("/account/security", "/account/authenticator/enable", ("code", Totp(await AuthenticatorKeyAsync())));
        Assert.Equal("/account/recovery-codes", RedirectTarget(enable));
    }

    private async Task<string> AuthenticatorKeyAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwsUser>>();
        var admin = await users.FindByNameAsync("admin") ?? throw new InvalidOperationException("no admin");
        return await users.GetAuthenticatorKeyAsync(admin) ?? throw new InvalidOperationException("no key");
    }

    private async Task<string> CreateInvitationAsync(TenantRole role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwsUser>>();
        var admin = await users.FindByNameAsync("admin") ?? throw new InvalidOperationException("no admin");
        var access = scope.ServiceProvider.GetRequiredService<AccessContext>();
        access.SetUser(admin.Id, "admin", strongSession: true, isOwsAdmin: true);
        access.SetTenant(admin.CurrentTenantId!.Value, "Acme", TenantRole.Owner);
        return await scope.ServiceProvider.GetRequiredService<CreateInvitation>().ExecuteAsync(role, "test", CancellationToken.None);
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) as authenticator apps compute it.</summary>
    private static string Totp(string base32Key)
    {
        var key = Base32Decode(base32Key);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }

    private static string RedirectTarget(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException($"No redirect: {(int)response.StatusCode}");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private Task<HttpResponseMessage> PostFormAsync(string formPage, string action, params (string Key, string Value)[] fields) =>
        PostFormAsync(formPage, action, _client, fields);

    /// <summary>Loads a page to obtain an antiforgery token, then posts the form like a browser would.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        string formPage, string action, HttpClient client, params (string Key, string Value)[] fields)
    {
        var html = await (await client.GetAsync(formPage)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken().Match(html).Groups[1].Value;
        return await client.PostAsync(action, new FormUrlEncodedContent(fields
            .Select(f => KeyValuePair.Create(f.Key, f.Value))
            .Append(KeyValuePair.Create("__RequestVerificationToken", token))));
    }

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex AntiforgeryToken();

    [GeneratedRegex(@"href=""([^""]*app[^""]*\.css)""")]
    private static partial Regex StylesheetHref();

    [GeneratedRegex(@"<li>[0-9A-Z]{5}-[0-9A-Z]{5}</li>")]
    private static partial Regex RecoveryCode();

    private static class AccountTestCounts
    {
        public const int RecoveryCodes = Web.Auth.AccountEndpoints.RecoveryCodeCount;
    }

    private sealed class TestFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();
        private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "ows-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Default", "Host=unused");
            builder.UseSetting("DataProtection:KeysPath", _keysPath);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll(typeof(IDbContextOptionsConfiguration<AppDbContext>));
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_databaseName));
            });
        }
    }
}
