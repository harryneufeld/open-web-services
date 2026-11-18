using OpenWebServices.Web.Services;

namespace OpenWebServices.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData("2a01:4f8:c015:c33e::/64", "2a01:4f8:c015:c33e::1")]
    [InlineData("2a01:4f8:c015:c33e::", "2a01:4f8:c015:c33e::1")]
    [InlineData("2001:db8::7", "2001:db8::7")]
    public void Ipv6_networks_resolve_to_their_first_address(string value, string expected) =>
        Assert.Equal(expected, Formatting.Ipv6Address(value));
}

public class KeyCommandsTests
{
    [Fact]
    public void Unix_commands_protect_the_key_and_test_it_alone()
    {
        var c = OpenWebServices.Web.Services.KeyCommands.For(OpenWebServices.Web.Services.ClientOs.Unix, "id_ed25519_laptop");

        Assert.Equal("mkdir -p ~/.ssh && mv ~/Downloads/id_ed25519_laptop ~/.ssh/ && chmod 600 ~/.ssh/id_ed25519_laptop && ssh-keygen -p -f ~/.ssh/id_ed25519_laptop", c.Install);
        Assert.StartsWith("ssh -o IdentitiesOnly=yes -i ~/.ssh/id_ed25519_laptop ", c.Test, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_commands_use_powershell_paths_and_a_private_acl()
    {
        var c = OpenWebServices.Web.Services.KeyCommands.For(OpenWebServices.Web.Services.ClientOs.Windows, "id_ed25519_laptop");

        Assert.Contains("""Move-Item "$HOME\Downloads\id_ed25519_laptop" "$HOME\.ssh\id_ed25519_laptop";""", c.Install, StringComparison.Ordinal);
        Assert.Contains("""icacls "$HOME\.ssh\id_ed25519_laptop" /inheritance:r /grant:r "${env:USERNAME}:F";""", c.Install, StringComparison.Ordinal);
        Assert.EndsWith("""ssh-keygen -p -f "$HOME\.ssh\id_ed25519_laptop" """.TrimEnd(), c.Install, StringComparison.Ordinal);
        Assert.Contains("IdentitiesOnly=yes", c.Test, StringComparison.Ordinal);
    }
}
