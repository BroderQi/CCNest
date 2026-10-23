using System.Reflection;

namespace CCNest.Core;

public static class AppInfo
{
    public const string Name = "CCNest";
    public const string DisplayName = "CCNest · Claude 环境管家";
    public const string BrowserProfileDisplayName = "CCNest Claude专属";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";
}
