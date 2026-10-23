using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CCNest.Core.SystemEnv;

public enum EnvScope { User, Machine, Process }

public sealed record EnvVarValue(string Name, EnvScope Scope, string Value);

/// <summary>
/// Persistent environment variables (HKCU\Environment for the user, the Session Manager key for the
/// machine). User-scope changes are broadcast with WM_SETTINGCHANGE("Environment") so Explorer passes
/// them to programs started afterwards; already-running programs keep their old environment.
/// </summary>
public static class EnvironmentVars
{
    public static readonly string[] ProxyVariables =
    {
        "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY", "https_proxy", "http_proxy", "all_proxy", "no_proxy",
    };

    private const string UserKey = "Environment";
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>Tests redirect user-scope reads/writes to a sandbox key under HKCU.</summary>
    public static string UserKeyPath { get; set; } = UserKey;
    public static bool BroadcastChanges { get; set; } = true;

    public static string? GetUser(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(UserKeyPath);
        return FindValue(k, name);
    }

    public static string? GetMachine(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(MachineKey);
        return FindValue(k, name);
    }

    private static string? FindValue(RegistryKey? k, string name)
    {
        if (k == null) return null;
        // Environment variable names are case-insensitive on Windows.
        var actual = k.GetValueNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return actual == null ? null : k.GetValue(actual, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static void SetUser(string name, string? value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(UserKeyPath, writable: true);
        var existing = k.GetValueNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (value == null)
        {
            if (existing != null) k.DeleteValue(existing, throwOnMissingValue: false);
        }
        else
        {
            if (existing != null && existing != name) k.DeleteValue(existing, false);
            k.SetValue(name, value, RegistryValueKind.String);
        }
        if (BroadcastChanges) Broadcast("Environment");
    }

    /// <summary>All proxy-related variables at each scope (names deduplicated case-insensitively).</summary>
    public static IReadOnlyList<EnvVarValue> ProxyVariablesEverywhere()
    {
        var list = new List<EnvVarValue>();
        foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY" })
        {
            if (GetUser(name) is { } u) list.Add(new EnvVarValue(name, EnvScope.User, u));
            if (GetMachine(name) is { } m) list.Add(new EnvVarValue(name, EnvScope.Machine, m));
            if (Environment.GetEnvironmentVariable(name) is { } p) list.Add(new EnvVarValue(name, EnvScope.Process, p));
        }
        return list;
    }

    public static void Broadcast(string area)
    {
        const int HWND_BROADCAST = 0xffff;
        const int WM_SETTINGCHANGE = 0x001A;
        const int SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 3000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}
