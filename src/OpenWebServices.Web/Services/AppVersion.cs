using System.Reflection;

namespace OpenWebServices.Web.Services;

/// <summary>The running ows version, set at build time from git (see version.json), e.g. "0.1.12".</summary>
public static class AppVersion
{
    /// <summary>Version without build metadata: "0.1.12", or "0.1.12-g1a2b3c4d" for builds outside main.</summary>
    public static string Display { get; } = Read().Split('+')[0];

    /// <summary>Full version including the commit id, e.g. "0.1.12+1a2b3c4d5e".</summary>
    public static string Full { get; } = Read();

    private static string Read() =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}
