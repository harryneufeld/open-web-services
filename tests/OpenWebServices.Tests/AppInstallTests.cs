using OpenWebServices.Domain;
using OpenWebServices.Domain.Apps;

namespace OpenWebServices.Tests;

public class AppInstallTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Successful_lifecycle_ends_installed()
    {
        var install = AppInstall.Request(TestData.TenantId, Guid.NewGuid(), "nginx", Now);

        install.Start();
        install.AppendLog("done");
        install.Complete(Now);

        Assert.Equal(AppInstallStatus.Installed, install.Status);
        Assert.Contains("done", install.Log);
        Assert.Equal(Now, install.FinishedAtUtc);
    }

    [Fact]
    public void Failure_is_written_to_log()
    {
        var install = AppInstall.Request(TestData.TenantId, Guid.NewGuid(), "nginx", Now);
        install.Start();

        install.Fail("boom", Now);

        Assert.Equal(AppInstallStatus.Failed, install.Status);
        Assert.Contains("boom", install.Log);
    }

    [Fact]
    public void Cannot_start_twice()
    {
        var install = AppInstall.Request(TestData.TenantId, Guid.NewGuid(), "nginx", Now);
        install.Start();

        Assert.Throws<DomainException>(install.Start);
    }

    [Fact]
    public void Cannot_complete_before_start()
    {
        var install = AppInstall.Request(TestData.TenantId, Guid.NewGuid(), "nginx", Now);

        Assert.Throws<DomainException>(() => install.Complete(Now));
    }
}
