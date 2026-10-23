using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CCNest.Core.Infrastructure;
using CCNest.Core.Settings;

namespace CCNest.Core.Browser;

public sealed record DedicatedProfileInfo(
    BrowserInstall? Browser, string UserDataDir, bool Exists, bool Running, long SizeBytes, DateTime? CreatedAt, string? CreatedWith);

/// <summary>
/// The permanent "Claude专属" browser environment: a separate Chromium user-data directory under
/// %LOCALAPPDATA%\CCNest\browser\&lt;engine&gt;. A separate user-data-dir gives full isolation from the
/// user's normal profiles (cookies, extensions, history, settings) while using the regular, auto-updated
/// browser installation. CCNest never deletes or resets this profile automatically.
///
/// Every launch passes --proxy-server pointing at CCNest's local port. When CCNest is not active that
/// port is closed, so the dedicated browser cannot accidentally load Claude through another route.
/// No fingerprint spoofing is performed: language preferences are ordinary browser settings and the
/// WebRTC policy is Chrome's own documented privacy option.
/// </summary>
public static class DedicatedBrowser
{
    private const string MarkerFile = "ccnest-profile.json";

    public static string UserDataDirFor(BrowserInstall browser) =>
        Path.Combine(AppPaths.BrowserRoot, browser.Kind == BrowserKind.Custom ? "custom" : browser.Kind.ToString().ToLowerInvariant(), "UserData");

    public static DedicatedProfileInfo Inspect(BrowserSettings settings, bool computeSize = true)
    {
        var browser = BrowserLocator.Resolve(settings);
        var dir = browser != null ? UserDataDirFor(browser) : Path.Combine(AppPaths.BrowserRoot, "UserData");
        var exists = Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        DateTime? created = null;
        string? createdWith = null;
        var marker = Path.Combine(dir, MarkerFile);
        if (File.Exists(marker))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(marker));
                created = j?["createdAt"]?.GetValue<DateTime>();
                createdWith = j?["browser"]?.GetValue<string>();
            }
            catch { /* ignore */ }
        }
        return new DedicatedProfileInfo(browser, dir, exists, exists && IsRunning(dir), computeSize && exists ? DirSize(dir) : 0, created, createdWith);
    }

    /// <summary>Chromium holds an exclusive lock on "lockfile" while a profile is open.</summary>
    public static bool IsRunning(string userDataDir)
    {
        var lockFile = Path.Combine(userDataDir, "lockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Creates the profile skeleton on first use (only if it does not exist yet).</summary>
    public static void EnsureProfile(BrowserInstall browser, BrowserSettings settings)
    {
        var dir = UserDataDirFor(browser);
        var defaultDir = Path.Combine(dir, "Default");
        var marker = Path.Combine(dir, MarkerFile);
        if (Directory.Exists(defaultDir) && File.Exists(Path.Combine(defaultDir, "Preferences")))
        {
            if (!File.Exists(marker)) WriteMarker(marker, browser);
            return;
        }

        Directory.CreateDirectory(defaultDir);
        var prefs = new JsonObject
        {
            ["intl"] = new JsonObject { ["accept_languages"] = settings.AcceptLanguages, ["selected_languages"] = settings.AcceptLanguages },
            ["profile"] = new JsonObject { ["name"] = AppInfo.BrowserProfileDisplayName, ["exit_type"] = "Normal" },
            ["browser"] = new JsonObject { ["check_default_browser"] = false },
            ["webrtc"] = new JsonObject { ["ip_handling_policy"] = settings.RestrictWebRtcToProxy ? "disable_non_proxied_udp" : "default" },
        };
        File.WriteAllText(Path.Combine(defaultDir, "Preferences"), prefs.ToJsonString());
        var localState = new JsonObject
        {
            ["intl"] = new JsonObject { ["app_locale"] = settings.UiLanguage },
            ["profile"] = new JsonObject
            {
                ["info_cache"] = new JsonObject { ["Default"] = new JsonObject { ["name"] = AppInfo.BrowserProfileDisplayName, ["is_using_default_name"] = false } },
            },
            ["browser"] = new JsonObject { ["has_seen_welcome_page"] = true },
        };
        File.WriteAllText(Path.Combine(dir, "Local State"), localState.ToJsonString());
        WriteMarker(marker, browser);
        Log.Info("Browser", $"已创建专属浏览器资料：{dir}");
    }

    /// <summary>
    /// Updates the language preference of an existing profile (only while the browser is closed, as
    /// Chromium rewrites Preferences on exit). Unknown keys are preserved.
    /// </summary>
    public static string? ApplyLanguagePreference(BrowserInstall browser, BrowserSettings settings)
    {
        var dir = UserDataDirFor(browser);
        if (IsRunning(dir)) return "专属浏览器正在运行，请先关闭后再修改语言设置。";
        var prefsPath = Path.Combine(dir, "Default", "Preferences");
        if (!File.Exists(prefsPath)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(prefsPath)) as JsonObject ?? new JsonObject();
            var backup = Path.Combine(AppPaths.BackupsDir, $"browser-preferences-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(prefsPath, backup, overwrite: true);
            if (root["intl"] is not JsonObject intl) root["intl"] = intl = new JsonObject();
            intl["accept_languages"] = settings.AcceptLanguages;
            intl["selected_languages"] = settings.AcceptLanguages;
            if (root["webrtc"] is not JsonObject webrtc) root["webrtc"] = webrtc = new JsonObject();
            webrtc["ip_handling_policy"] = settings.RestrictWebRtcToProxy ? "disable_non_proxied_udp" : "default";
            AtomicFile.WriteAllText(prefsPath, root.ToJsonString());
            return null;
        }
        catch (Exception ex)
        {
            return "无法更新浏览器偏好设置：" + ex.Message;
        }
    }

    public static IReadOnlyList<string> BuildArguments(BrowserInstall browser, BrowserSettings settings, int proxyPort, string? url)
    {
        var args = new List<string>
        {
            $"--user-data-dir={UserDataDirFor(browser)}",
            "--profile-directory=Default",
            $"--proxy-server=http://127.0.0.1:{proxyPort}",
            $"--lang={settings.UiLanguage}",
            "--no-first-run",
            "--no-default-browser-check",
        };
        if (settings.RestrictWebRtcToProxy) args.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        if (!string.IsNullOrEmpty(url)) args.Add(url);
        return args;
    }

    /// <summary>Launches (or focuses) the dedicated browser. Returns an error message or null.</summary>
    public static string? Launch(BrowserSettings settings, int proxyPort, string? url = "https://claude.ai/")
    {
        var browser = BrowserLocator.Resolve(settings);
        if (browser == null) return "未找到 Chrome 或 Edge 浏览器。请安装 Google Chrome 或在设置中指定浏览器路径。";
        EnsureProfile(browser, settings);
        var psi = new ProcessStartInfo(browser.Path) { UseShellExecute = false };
        foreach (var a in BuildArguments(browser, settings, proxyPort, url)) psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi);
            Log.Info("Browser", $"已启动专属浏览器（{browser.Name}），代理端口 {proxyPort}");
            return null;
        }
        catch (Exception ex)
        {
            return "启动浏览器失败：" + ex.Message;
        }
    }

    /// <summary>Creates a desktop shortcut that opens the dedicated browser through CCNest.</summary>
    public static string? CreateDesktopShortcut(BrowserInstall? browser)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var link = Path.Combine(desktop, "Claude专属浏览器 (CCNest).lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 不可用");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exe;
            shortcut.Arguments = "--open-browser";
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.Description = "通过 CCNest 打开 Claude 专属浏览器";
            shortcut.IconLocation = (browser?.Path ?? exe) + ",0";
            shortcut.Save();
            return null;
        }
        catch (Exception ex)
        {
            return "创建快捷方式失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Explicit, user-confirmed reset: the profile is moved aside (not deleted) so it can be recovered.
    /// </summary>
    public static string ResetProfile(BrowserInstall browser)
    {
        var dir = UserDataDirFor(browser);
        if (IsRunning(dir)) throw new InvalidOperationException("请先关闭专属浏览器");
        var archive = Path.Combine(AppPaths.BrowserRoot, $"archived-{browser.Kind.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.Move(dir, archive);
        Log.Info("Browser", $"专属浏览器资料已移至 {archive}");
        return archive;
    }

    public static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static void WriteMarker(string marker, BrowserInstall browser)
    {
        var node = new JsonObject
        {
            ["createdAt"] = DateTime.Now,
            ["browser"] = browser.Name,
            ["note"] = "CCNest dedicated Claude browser profile. CCNest never deletes this folder automatically.",
        };
        File.WriteAllText(marker, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static long DirSize(string dir)
    {
        long size = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try { size += new FileInfo(f).Length; } catch { /* locked */ }
            }
        }
        catch { /* ignore */ }
        return size;
    }
}
