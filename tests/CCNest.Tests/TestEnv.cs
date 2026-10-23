using CCNest.Core.Infrastructure;

namespace CCNest.Tests;

internal static class TestEnv
{
    private static readonly Lazy<string?> Mihomo = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "mihomo", "mihomo.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    });

    public static string? MihomoPath => Mihomo.Value;

    /// <summary>Isolated CCNest data root for a test (never touches the real %LOCALAPPDATA%\CCNest).</summary>
    public static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccnest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static readonly string GlobalRoot = NewTempRoot();

    /// <summary>Runs before any test: isolate all CCNest data paths and disable file logging.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        Log.FileEnabled = false;
        AppPaths.OverrideRoot(GlobalRoot);
        // Never touch the real user environment, Run key or system proxy from tests.
        Core.SystemEnv.StartupRegistration.RunKey = @"Software\CCNestTests\Run";
        Core.SystemEnv.EnvironmentVars.UserKeyPath = @"Software\CCNestTests\Environment";
        Core.SystemEnv.EnvironmentVars.BroadcastChanges = false;
        Core.SystemEnv.SystemProxy.Backend = new FakeProxyBackend();
        Core.Claude.ClaudeCodeManager.SettingsPathOverride = Path.Combine(GlobalRoot, "claude-settings.json");
    }
}
