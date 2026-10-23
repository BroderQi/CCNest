using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CCNest.Core.Infrastructure;
using CCNest.Core.Native;

namespace CCNest.Core.Claude;

public sealed record ClaudeDesktopInfo
{
    public bool Installed { get; init; }
    public string? Kind { get; init; }            // "MSIX" / "Squirrel"
    public string? ExecutablePath { get; init; }
    public string? AppUserModelId { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<int> RunningPids { get; init; } = Array.Empty<int>();
    public string? ConfigDir { get; init; }
}

/// <summary>
/// Claude Desktop (Electron). Chromium networking inside Electron follows the Windows system proxy
/// and also honours the --proxy-server switch, which CCNest passes when it launches the app.
/// Note: when Claude Desktop is already running, a second launch only focuses the existing window
/// and the switch is ignored, so CCNest asks the user to quit it first.
/// </summary>
public static class ClaudeDesktopManager
{
    public static string SquirrelRoot => Path.Combine(AppPaths.LocalAppData, "AnthropicClaude");
    public static string ConfigDir => Path.Combine(AppPaths.RoamingAppData, "Claude");

    public static async Task<ClaudeDesktopInfo> DetectAsync(CancellationToken ct = default)
    {
        var running = RunningPids();

        var squirrelExe = LatestSquirrelExe();
        if (squirrelExe != null)
        {
            return new ClaudeDesktopInfo
            {
                Installed = true,
                Kind = "Squirrel",
                ExecutablePath = squirrelExe,
                Version = Path.GetFileName(Path.GetDirectoryName(squirrelExe))?.Replace("app-", ""),
                RunningPids = running,
                ConfigDir = Directory.Exists(ConfigDir) ? ConfigDir : null,
            };
        }

        // MSIX package (current distribution format).
        const string script = """
            $p = Get-AppxPackage | Where-Object { $_.Name -like '*Claude*' -and $_.Publisher -like '*Anthropic*' } | Select-Object -First 1
            if ($p) {
              $app = Get-StartApps | Where-Object { $_.AppID -like ($p.PackageFamilyName + '!*') } | Select-Object -First 1
              [pscustomobject]@{ Name=$p.Name; Version=$p.Version.ToString(); Location=$p.InstallLocation; Aumid=$app.AppID } | ConvertTo-Json -Compress
            }
            """;
        try
        {
            var r = await ProcessRunner.PowerShellAsync(script, timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
            if (r.Success && r.StdOut.Trim().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(r.StdOut.Trim());
                var root = doc.RootElement;
                string? S(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var location = S("Location");
                var exe = location != null ? Directory.EnumerateFiles(location, "claude.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
                return new ClaudeDesktopInfo
                {
                    Installed = true,
                    Kind = "MSIX",
                    ExecutablePath = exe,
                    AppUserModelId = S("Aumid"),
                    Version = S("Version"),
                    RunningPids = running,
                    ConfigDir = Directory.Exists(ConfigDir) ? ConfigDir : null,
                };
            }
        }
        catch (Exception ex)
        {
            Log.Debug("ClaudeDesktop", $"MSIX 检测失败：{ex.Message}");
        }

        return new ClaudeDesktopInfo { Installed = false, RunningPids = running };
    }

    private static string? LatestSquirrelExe()
    {
        if (!Directory.Exists(SquirrelRoot)) return null;
        var latest = Directory.EnumerateDirectories(SquirrelRoot, "app-*")
            .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : new Version(0, 0)))
            .OrderByDescending(x => x.ver)
            .Select(x => Path.Combine(x.dir, "claude.exe"))
            .FirstOrDefault(File.Exists);
        return latest ?? (File.Exists(Path.Combine(SquirrelRoot, "claude.exe")) ? Path.Combine(SquirrelRoot, "claude.exe") : null);
    }

    public static IReadOnlyList<int> RunningPids()
    {
        var list = new List<int>();
        foreach (var p in ClaudeCodeManager.SafeProcesses("claude"))
        {
            var path = IpHelper.GetProcessPath(p.Id) ?? "";
            if (path.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase) ||
                (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) && path.Contains("Claude", StringComparison.OrdinalIgnoreCase)))
                list.Add(p.Id);
        }
        return list;
    }

    public static string ProxySwitch(int port) => $"--proxy-server=http://127.0.0.1:{port}";

    /// <summary>Starts Claude Desktop with an explicit proxy switch. Returns an error message or null.</summary>
    public static string? LaunchWithProxy(ClaudeDesktopInfo info, int port)
    {
        if (!info.Installed) return "未检测到 Claude Desktop";
        if (RunningPids().Count > 0) return "Claude Desktop 正在运行。请先从托盘图标完全退出 Claude Desktop，再由 CCNest 启动，否则代理参数不会生效。";
        try
        {
            if (info.Kind == "MSIX" && info.AppUserModelId != null)
            {
                var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                mgr.ActivateApplication(info.AppUserModelId, ProxySwitch(port), 0, out _);
                return null;
            }
            if (info.ExecutablePath != null)
            {
                var psi = new ProcessStartInfo(info.ExecutablePath) { UseShellExecute = false };
                psi.ArgumentList.Add(ProxySwitch(port));
                Process.Start(psi);
                return null;
            }
            return "无法确定 Claude Desktop 的启动方式";
        }
        catch (Exception ex)
        {
            Log.Error("ClaudeDesktop", "启动失败", ex);
            return $"启动失败：{ex.Message}";
        }
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        void ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        void ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
}
