using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Tests;

public class ServerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Server NewServer(string name = "web-1") =>
        Server.Provision(TestData.TenantId, Guid.NewGuid(), name, "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR", "key", TestKeys.Ed25519Line(200), Now);

    private static Server RunningServer()
    {
        var server = NewServer();
        server.AttachProviderResources("1", "2", "203.0.113.10");
        server.ApplyObserved(ObservedServerState.Running, null, Now);
        return server;
    }

    [Fact]
    public void Provision_starts_in_provisioning_state()
    {
        var server = NewServer();

        Assert.Equal(ServerStatus.Provisioning, server.Status);
        Assert.NotEqual(Guid.Empty, server.Id);
        Assert.False(server.IsReachable);
        Assert.True(server.IsTransitioning);
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
    public void Provision_requires_a_group() =>
        Assert.Throws<DomainException>(() =>
            Server.Provision(TestData.TenantId, Guid.Empty, "web", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR", "key", TestKeys.Ed25519Line(200), Now));

    [Fact]
    public void Running_server_with_ip_is_reachable_and_can_get_apps()
    {
        var server = RunningServer();

        Assert.True(server.IsReachable);
        Assert.True(server.CanInstallApps);
        Assert.Equal("203.0.113.10", server.PublicIpv4);
    }

    [Fact]
    public void Observed_state_keeps_known_ip_when_provider_reports_none()
    {
        var server = RunningServer();

        server.ApplyObserved(ObservedServerState.Running, null, Now);

        Assert.Equal("203.0.113.10", server.PublicIpv4);
    }

    [Fact]
    public void Provisioning_stays_provisioning_while_provider_is_still_starting()
    {
        var server = NewServer();

        server.ApplyObserved(ObservedServerState.Starting, null, Now);

        Assert.Equal(ServerStatus.Provisioning, server.Status);
    }

    [Fact]
    public void Stopping_wins_over_a_stale_running_report_during_the_grace_period()
    {
        var server = RunningServer();
        server.MarkStopping(Now);

        server.ApplyObserved(ObservedServerState.Running, null, Now.AddSeconds(5));
        Assert.Equal(ServerStatus.Stopping, server.Status);

        server.ApplyObserved(ObservedServerState.Stopped, null, Now.AddSeconds(10));
        Assert.Equal(ServerStatus.Stopped, server.Status);
        Assert.Null(server.PendingSinceUtc);
    }

    [Fact]
    public void Provider_state_wins_once_the_grace_period_is_over()
    {
        var server = RunningServer();
        server.MarkStopping(Now);

        server.ApplyObserved(ObservedServerState.Running, null, Now + Server.PendingGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(ServerStatus.Running, server.Status);
    }

    [Fact]
    public void Running_report_clears_a_previous_failure()
    {
        var server = RunningServer();
        server.MarkFailed("gone");

        server.ApplyObserved(ObservedServerState.Running, null, Now);

        Assert.Equal(ServerStatus.Running, server.Status);
        Assert.Null(server.FailureReason);
    }

    [Fact]
    public void Deleting_server_ignores_observed_state_and_power_actions()
    {
        var server = RunningServer();
        server.MarkDeleting();

        server.ApplyObserved(ObservedServerState.Running, null, Now);

        Assert.Equal(ServerStatus.Deleting, server.Status);
        Assert.Throws<DomainException>(() => server.MarkStarting(Now));
    }

    [Fact]
    public void Imported_server_takes_the_observed_state_and_cannot_get_apps()
    {
        var server = Server.Import(TestData.TenantId, Guid.NewGuid(), "99", "Legacy Box", ObservedServerState.Running, "198.51.100.1",
            "nbg1", "cx22", "debian-12", 3.5m, "EUR", Now);

        Assert.Equal(ServerOrigin.Imported, server.Origin);
        Assert.Equal(ServerStatus.Running, server.Status);
        Assert.True(server.IsReachable);
        Assert.False(server.CanInstallApps);
    }

    [Fact]
    public void Imported_server_in_unknown_state_is_not_shown_as_busy()
    {
        var server = Server.Import(TestData.TenantId, Guid.NewGuid(), "99", "box", ObservedServerState.Unknown, null,
            "nbg1", "cx22", "debian-12", 3.5m, "EUR", Now);

        Assert.False(server.IsTransitioning);
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
