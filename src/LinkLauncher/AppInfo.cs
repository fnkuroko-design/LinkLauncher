using System.Reflection;

namespace LinkLauncher;

internal static class AppInfo
{
    // The csproj Version is the only source of the application's displayed version.
    public static string Version { get; } = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string BuildLabel => Version.Contains('-') ? Version[(Version.IndexOf('-') + 1)..] : "v" + Version;
}
