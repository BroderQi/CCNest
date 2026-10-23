using Microsoft.Win32;

namespace CCNest.Core.SystemEnv;

/// <summary>
/// While a session is active CCNest registers itself under HKCU\...\Run with "--recover", so that if
/// Windows restarts or CCNest crashes, the user is shown the recovery dialog at next login instead of
/// being left with a silently modified system. The entry is removed when the session ends normally.
/// </summary>
public static class StartupRegistration
{
    /// <summary>Tests redirect this to a sandbox key.</summary>
    public static string RunKey { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CCNest.Recovery";

    public static string? ExecutablePath { get; set; } = Environment.ProcessPath;

    public static void AddRecovery()
    {
        if (ExecutablePath == null) return;
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        k.SetValue(ValueName, $"\"{ExecutablePath}\" --recover", RegistryValueKind.String);
    }

    public static void RemoveRecovery()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsRecoveryRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) != null;
    }
}
