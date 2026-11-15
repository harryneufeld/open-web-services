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
