using System.Buffers.Binary;
using OpenWebServices.Infrastructure.Security;

namespace OpenWebServices.Tests;

public class RsaSshKeyGeneratorTests
{
    [Fact]
    public void Public_key_is_a_well_formed_openssh_rsa_line()
    {
        var pair = new RsaSshKeyGenerator().Generate("test@ows");

        var parts = pair.PublicKeyOpenSsh.Split(' ');
        Assert.Equal(["ssh-rsa", parts[1], "test@ows"], parts);

        var blob = Convert.FromBase64String(parts[1]);
        var typeLength = BinaryPrimitives.ReadInt32BigEndian(blob);
        Assert.Equal("ssh-rsa", System.Text.Encoding.ASCII.GetString(blob, 4, typeLength));
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", pair.PrivateKeyPem, StringComparison.Ordinal);
    }
}
