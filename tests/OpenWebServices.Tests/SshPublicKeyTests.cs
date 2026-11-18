using OpenWebServices.Application.Access;
using OpenWebServices.Application.Servers;
using OpenWebServices.Domain;
using OpenWebServices.Domain.SshKeys;

namespace OpenWebServices.Tests;

public class SshPublicKeyTests
{
    // Throwaway public keys made with ssh-keygen; fingerprints as `ssh-keygen -l` printed them.
    internal const string Ed25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJSdvXgK+yrTgB8M10/+j5Vr3KGYerPwNffBOcvEAGck alice@laptop";
    private const string Ed25519Fingerprint = "SHA256:jPHr5fH0FKaeayIpCod4zEVLDxrXaB0DhkEQEQcfBvM";

    internal const string Rsa3072 = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQDgd/a8yXrIwY1uJCeVxIYN6sCH7r8NRxvKQOVBvD3o5MvX3Kxj0rcMi4RKMjfhRB8suWhw6OU9NPzse9AFjHwXQ+AiFyVEXxVMzjgUrHtwOEYfAXatwWErFIrnerzREZhZXjaeHzXzy7S1QrBa+Dmk7K5sJUyYK8j4pbp05w73gzlX3R3/AUx3K4XoANsKeZwYa5FXVJpErZ5dS9qH6kS66GMOSWWupoS2xnYeK9UrE1Jke1kHLz8MIzlnRi60Tj15IJSpmesRyvte+it/aQokmJUY9EHR6rDmuccQRgOz5tRb3EDKEfPmLNvlOVdejeV8l5/kstfXXsTKWyPRzhEj96uriR0FylXBrW24VLMK1j53mospdmNMkanLS3al1f/84/Ur8TcnGg1PIzuwnx/QD0Zf70ioV538CGgT8ElpVfD6IiQIiNbqadZArJg7mSk6loqpnIG9Uk4HsBliCeSV48yqoxfdjw0xKSaJUPEXf1zc4L9TRYHvxg366WysxZk= ci";
    private const string Rsa3072Fingerprint = "SHA256:R5C0PUPJ3K1frJDP2A7gHyyvsDISfQdqrnNUeDUdGpQ";

    private const string Rsa1024 = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAAAgQC/CM7z7ELf5YIQN3FAU+MF3hsNrk+O6lk/jCDsD3TjOA3M8hY9G4P9HjuFjQwRWav1zFJo489aBouxcFe1TAMBFQl2nCuKdBY2rejz11kSjQeIG0DF3AW82x3IfkRQb+7k9l/i82nSEm8npPlIvRM7d5sk8QRboAYk1DaRGEydAw== old";

    private const string Ecdsa = "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBGt2JQkg+cV3iMfKcO3GytTYIIyfrArJSKn/set7zit9EIeARVGPqEtErRSoh6uTyAtaJUs5NL1LHBRWzUroXLQ=";
    private const string EcdsaFingerprint = "SHA256:nieCQ7O7lhOd81fBf+WA+b5qvVTHvhD7CVAe+enGiUQ";

    [Theory]
    [InlineData(Ed25519, "ED25519", 256, Ed25519Fingerprint)]
    [InlineData(Rsa3072, "RSA", 3072, Rsa3072Fingerprint)]
    [InlineData(Ecdsa, "ECDSA", 256, EcdsaFingerprint)]
    public void Parses_keys_with_the_fingerprint_ssh_keygen_prints(string line, string algorithm, int bits, string fingerprint)
    {
        var key = SshPublicKey.Parse("  " + line + "\n");

        Assert.Equal(algorithm, key.Algorithm);
        Assert.Equal(bits, key.Bits);
        Assert.Equal(fingerprint, key.Fingerprint);
    }

    [Fact]
    public void Keeps_the_comment_and_can_relabel_the_line()
    {
        var key = SshPublicKey.Parse(Ed25519);

        Assert.Equal("alice@laptop", key.Comment);
        Assert.Equal(Ed25519.Replace("alice@laptop", "laptop (ows)", StringComparison.Ordinal), key.ToLine("laptop (ows)"));
    }

    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----", "private key")]
    [InlineData("", "Paste a public key")]
    [InlineData("ssh-ed25519", "has the form")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP", "DSA")]
    [InlineData("ssh-foo AAAAC3NzaC1lZDI1NTE5AAAAIJSdvXgK", "Unsupported key type")]
    [InlineData("ssh-ed25519 not-base64!", "not valid base64")]
    [InlineData(Rsa1024, "1024 bits")]
    public void Rejects_what_is_not_a_safe_public_key(string text, string message)
    {
        var ex = Assert.Throws<DomainException>(() => SshPublicKey.Parse(text));
        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_blob_of_another_type_than_declared()
    {
        var blob = Ed25519.Split(' ')[1];
        var ex = Assert.Throws<DomainException>(() => SshPublicKey.Parse($"ssh-rsa {blob}"));
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_several_keys_at_once()
    {
        Assert.Throws<DomainException>(() => SshPublicKey.Parse(Ed25519 + "\n" + Ecdsa));
    }

    [Fact]
    public void Finds_the_key_behind_authorized_keys_options()
    {
        var line = "no-port-forwarding,no-agent-forwarding,command=\"echo 'Please login as the user \\\"ubuntu\\\" rather than root.';sleep 10\" " + Ed25519;

        var key = SshPublicKey.FindInAuthorizedKeysLine(line);

        Assert.Equal(Ed25519Fingerprint, key!.Fingerprint);
        Assert.Null(SshPublicKey.FindInAuthorizedKeysLine("# a comment " + Ed25519));
        Assert.Null(SshPublicKey.FindInAuthorizedKeysLine("   "));
    }

    [Fact]
    public void Control_characters_in_comments_are_flattened()
    {
        var key = SshPublicKey.Parse(Ed25519.Replace("alice@laptop", "a\tb", StringComparison.Ordinal));
        Assert.Equal("a b", key.Comment);
    }
}

public class AuthorizedKeysFileTests
{
    private static readonly string Ows = TestKeys.Ed25519Line(1, "ows automation");
    private static readonly string Alice = TestKeys.Ed25519Line(2, "alice (ows)");
    private static readonly string Bob = TestKeys.Ed25519Line(3, "bob (ows)");
    private static readonly string Stranger = TestKeys.Ed25519Line(9, "stranger@somewhere");

    [Fact]
    public void Writes_a_marked_block_into_an_empty_file()
    {
        var result = AuthorizedKeysFile.Rewrite("", [Ows, Alice], []);

        Assert.Equal($"{AuthorizedKeysFile.BeginMarker}\n{Ows}\n{Alice}\n{AuthorizedKeysFile.EndMarker}\n", result.Content);
        Assert.True(result.Changed);
        Assert.Empty(result.UnknownLines);
    }

    [Fact]
    public void Keeps_foreign_lines_and_reports_unknown_keys()
    {
        var current = $"# my notes\n{Stranger}\n";

        var result = AuthorizedKeysFile.Rewrite(current, [Ows], []);

        Assert.StartsWith($"# my notes\n{Stranger}\n\n{AuthorizedKeysFile.BeginMarker}\n", result.Content, StringComparison.Ordinal);
        Assert.Equal([Stranger], result.UnknownLines);
    }

    [Fact]
    public void Moves_copies_of_managed_keys_into_the_block()
    {
        // The provider put the ows key into the file before ows managed it.
        var providerCopy = TestKeys.Ed25519Line(1, "ows-web-1");

        var result = AuthorizedKeysFile.Rewrite(providerCopy + "\n", [Ows], []);

        Assert.Equal($"{AuthorizedKeysFile.BeginMarker}\n{Ows}\n{AuthorizedKeysFile.EndMarker}\n", result.Content);
        Assert.Empty(result.UnknownLines);
    }

    [Fact]
    public void Removes_keys_everywhere_and_replaces_the_old_block()
    {
        var current = $"{Stranger}\n{AuthorizedKeysFile.BeginMarker}\n{Ows}\n{Bob}\n{AuthorizedKeysFile.EndMarker}\n";

        var result = AuthorizedKeysFile.Rewrite(current, [Ows, Alice], [TestKeys.Blob(Stranger), TestKeys.Blob(Bob)]);

        Assert.Equal($"{AuthorizedKeysFile.BeginMarker}\n{Ows}\n{Alice}\n{AuthorizedKeysFile.EndMarker}\n", result.Content);
        Assert.DoesNotContain("stranger", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_already_in_shape_is_not_rewritten()
    {
        var first = AuthorizedKeysFile.Rewrite("", [Ows, Alice], []).Content;

        var second = AuthorizedKeysFile.Rewrite(first, [Ows, Alice], []);

        Assert.False(second.Changed);
    }

    [Fact]
    public void Cloud_init_script_carries_the_block_and_keeps_root_reachable()
    {
        var script = ServerBootstrapScript.Build([Ows, Alice]);

        Assert.StartsWith("#cloud-config\n", script, StringComparison.Ordinal);
        Assert.Contains("disable_root: false", script, StringComparison.Ordinal);
        Assert.Contains("ssh_pwauth: false", script, StringComparison.Ordinal);
        Assert.Contains($"      {AuthorizedKeysFile.BeginMarker}\n      {Ows}\n      {Alice}\n      {AuthorizedKeysFile.EndMarker}\n", script, StringComparison.Ordinal);
        Assert.Contains("get.docker.com", script, StringComparison.Ordinal);
    }
}
