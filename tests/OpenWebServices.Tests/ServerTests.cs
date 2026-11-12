using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Tests;

public class ServerTests
{
    private static Server NewServer(string name = "web-1") =>
        Server.Provision(name, "hetzner", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR", "key", DateTime.UtcNow);

    [Fact]
    public void Provision_starts_in_provisioning_state()
    {
        var server = NewServer();

        Assert.Equal(ServerStatus.Provisioning, server.Status);
        Assert.NotEqual(Guid.Empty, server.Id);
        Assert.False(server.IsReachable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Web")]
    [InlineData("-web")]
    [InlineData("web-")]
    [InlineData("web_1")]
    [InlineData("with space")]
    public void Provision_rejects_invalid_names(string name) =>
        Assert.Throws<DomainException>(() => NewServer(name));

    [Fact]
    public void Running_server_with_ip_is_reachable()
    {
        var server = NewServer();

        server.MarkRunning("203.0.113.10");

        Assert.True(server.IsReachable);
        Assert.Equal("203.0.113.10", server.PublicIpv4);
    }

    [Fact]
    public void MarkRunning_keeps_known_ip_when_provider_reports_none()
    {
        var server = NewServer();
        server.AttachProviderResources(1, 2, "203.0.113.10");

        server.MarkRunning(null);

        Assert.Equal("203.0.113.10", server.PublicIpv4);
    }

    [Fact]
    public void Deleting_server_cannot_be_marked_running()
    {
        var server = NewServer();
        server.MarkDeleting();

        Assert.Throws<DomainException>(() => server.MarkRunning("203.0.113.10"));
    }

    [Fact]
    public void MarkFailed_records_reason()
    {
        var server = NewServer();

        server.MarkFailed("no capacity");

        Assert.Equal(ServerStatus.Failed, server.Status);
        Assert.Equal("no capacity", server.FailureReason);
    }
}
