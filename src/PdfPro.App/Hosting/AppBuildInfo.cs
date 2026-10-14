using System.Reflection;

namespace PdfPro.App.Hosting;

/// <summary>Facts about the running build.</summary>
public sealed record AppBuildInfo(string Version, bool IsDebugBuild)
{
    public static AppBuildInfo Current { get; } = new(
        typeof(AppBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
#if DEBUG
        IsDebugBuild: true);
#else
        IsDebugBuild: false);
#endif
}
