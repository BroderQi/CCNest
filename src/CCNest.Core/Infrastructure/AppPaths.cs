namespace CCNest.Core.Infrastructure;

/// <summary>
/// All persistent CCNest data lives under %LOCALAPPDATA%\CCNest and is never stored in the
/// install directory, so upgrades and reinstalls cannot destroy settings or browser data.
/// </summary>
public static class AppPaths
{
    private static string? _rootOverride;

    /// <summary>Tests redirect all data into a temporary folder.</summary>
    public static void OverrideRoot(string? root) => _rootOverride = root;

    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCNest");

    public static string ConfigDir => Ensure(Path.Combine(Root, "config"));
    public static string StateDir => Ensure(Path.Combine(Root, "state"));
    public static string LogsDir => Ensure(Path.Combine(Root, "logs"));
    public static string SecretsDir => Ensure(Path.Combine(Root, "secrets"));
    public static string BackupsDir => Ensure(Path.Combine(Root, "backups"));
    public static string RuntimeDir => Ensure(Path.Combine(Root, "runtime"));
    public static string MihomoHomeDir => Ensure(Path.Combine(Root, "runtime", "mihomo"));

    /// <summary>Permanent dedicated browser data. Never deleted automatically.</summary>
    public static string BrowserRoot => Ensure(Path.Combine(Root, "browser"));
    public static string BrowserUserDataDir => Path.Combine(BrowserRoot, "UserData");

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string SessionJournalFile => Path.Combine(StateDir, "session.json");
    public static string SystemEnvJournalFile => Path.Combine(StateDir, "system-env.json");
    public static string LastExitFile => Path.Combine(StateDir, "last-exit.json");

    public static string InstallDir => AppContext.BaseDirectory;

    /// <summary>Machine-wide directory used only by the elevated helper (admin-only ACL).</summary>
    public static string ElevatedCoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CCNest", "core");

    public static string BundledMihomoPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(InstallDir, "core", "mihomo.exe"),
                Path.Combine(InstallDir, "mihomo.exe"),
            };
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
