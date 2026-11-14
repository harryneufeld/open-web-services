using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Groups;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Tests;

public class CredentialMaskingTests
{
    private static readonly ProviderTypeInfo Type = new("test", "Test", "", ResourceKind.Servers,
    [
        new CredentialField("endpoint", "Endpoint", IsSecret: false),
        new CredentialField("token", "Token"),
        new CredentialField("pin", "PIN"),
        new CredentialField("unset", "Unset"),
    ]);

    [Fact]
    public void Mask_hides_secrets_and_shows_plain_fields()
    {
        var masked = GroupCredentials.Mask(Type, new Dictionary<string, string>
        {
            ["endpoint"] = "ovh-eu",
            ["token"] = "abcdefghijklmnop1234",
            ["pin"] = "short-secret",
        }).ToDictionary(c => c.Key, c => c.Display);

        Assert.Equal("ovh-eu", masked["endpoint"]);
        Assert.Equal("••••••••1234", masked["token"]);
        Assert.Equal("••••••••", masked["pin"]); // too short to reveal any part of it
        Assert.False(masked.ContainsKey("unset"));
    }
}
