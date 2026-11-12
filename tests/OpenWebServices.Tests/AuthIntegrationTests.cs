using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenWebServices.Infrastructure.Persistence;

namespace OpenWebServices.Tests;

public sealed partial class AuthIntegrationTests : IDisposable
{
    private const string StrongPassword = "correct horse battery";

    private readonly TestFactory _factory = new();
    private readonly HttpClient _client;

    public AuthIntegrationTests() =>
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

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
    public async Task Login_page_sends_to_setup_until_an_admin_exists()
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
    public async Task Setup_creates_the_admin_and_signs_in()
    {
        var setup = await PostFormAsync("/setup", "/auth/setup", ("username", "admin"), ("password", StrongPassword));

        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        Assert.Equal("/", RedirectTarget(setup));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Setup_rejects_short_passwords()
    {
        var response = await PostFormAsync("/setup", "/auth/setup", ("username", "admin"), ("password", "short"));

        Assert.StartsWith("/setup?error=", RedirectTarget(response), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await _client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Setup_cannot_be_repeated_once_an_admin_exists()
    {
        await PostFormAsync("/setup", "/auth/setup", ("username", "admin"), ("password", StrongPassword));
        using var attacker = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The setup page is hidden once an admin exists, so a direct POST borrows a token from the login page.
        var response = await PostFormAsync("/login", "/auth/setup", attacker, ("username", "evil"), ("password", StrongPassword));

        Assert.StartsWith("/setup?error=", RedirectTarget(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_stays_signed_out()
    {
        await PostFormAsync("/setup", "/auth/setup", ("username", "admin"), ("password", StrongPassword));
        using var visitor = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var login = await PostFormAsync("/login", "/auth/login", visitor, ("username", "admin"), ("password", "wrong-password!"));

        Assert.StartsWith("/login?error=1", RedirectTarget(login), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Correct_password_signs_in_and_logout_signs_out()
    {
        await PostFormAsync("/setup", "/auth/setup", ("username", "admin"), ("password", StrongPassword));
        using var visitor = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await PostFormAsync("/login", "/auth/login", visitor, ("username", "Admin"), ("password", StrongPassword));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/")).StatusCode);

        await PostFormAsync("/", "/auth/logout", visitor);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    public async Task Login_ignores_external_return_urls(string returnUrl)
    {
        await PostFormAsync("/setup", "/auth/setup", ("username", "admin"), ("password", StrongPassword));
        using var visitor = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var login = await PostFormAsync("/login", "/auth/login", visitor,
            ("username", "admin"), ("password", StrongPassword), ("returnUrl", returnUrl));

        Assert.Equal("/", RedirectTarget(login));
    }

    [Fact]
    public async Task Posting_without_an_antiforgery_token_is_refused()
    {
        var response = await _client.PostAsync("/auth/setup", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = "admin", ["password"] = StrongPassword }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string RedirectTarget(HttpResponseMessage response)
    {
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private Task<HttpResponseMessage> PostFormAsync(string formPage, string action, params (string Key, string Value)[] fields) =>
        PostFormAsync(formPage, action, _client, fields);

    /// <summary>Loads a page to obtain an antiforgery token and cookie, then posts the form like a browser would.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        string formPage, string action, HttpClient client, params (string Key, string Value)[] fields)
    {
        var page = await client.GetAsync(formPage);
        var cookies = string.Join("; ", page.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0])
            : []);
        var html = await page.Content.ReadAsStringAsync();
        var token = AntiforgeryToken().Match(html).Groups[1].Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, action)
        {
            Content = new FormUrlEncodedContent(fields
                .Select(f => KeyValuePair.Create(f.Key, f.Value))
                .Append(KeyValuePair.Create("__RequestVerificationToken", token))),
        };
        if (cookies.Length > 0)
            request.Headers.Add("Cookie", cookies);

        var response = await client.SendAsync(request);
        // Later requests on the same client must carry the auth cookie, so remember it.
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            var all = string.Join("; ", setCookies.Select(v => v.Split(';')[0]).Append(cookies).Where(c => c.Length > 0));
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", all);
        }

        return response;
    }

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex AntiforgeryToken();

    [GeneratedRegex(@"href=""([^""]*app[^""]*\.css)""")]
    private static partial Regex StylesheetHref();

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
