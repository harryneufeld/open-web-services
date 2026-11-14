using System.Text.RegularExpressions;
using OpenWebServices.Web.Services;

namespace OpenWebServices.Tests;

public class AppVersionTests
{
    [Fact]
    public void The_build_stamps_a_semantic_version_from_git() =>
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), AppVersion.Display);
}
