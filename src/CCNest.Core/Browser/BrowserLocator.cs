using CCNest.Core.Settings;
using Microsoft.Win32;

namespace CCNest.Core.Browser;

public sealed record BrowserInstall(BrowserKind Kind, string Name, string Path, string? Version);

/// <summary>Finds installed Chromium-based browsers (Chrome, Edge, Brave, Chromium).</summary>
public static class BrowserLocator
{
    private static readonly (BrowserKind Kind, string Name, string Exe, string[] Relative)[] Known =
    {
        (BrowserKind.Chrome, "Google Chrome", "chrome.exe", new[] { @"Google\Chrome\Application\chrome.exe" }),
        (BrowserKind.Edge, "Microsoft Edge", "msedge.exe", new[] { @"Microsoft\Edge\Application\msedge.exe" }),
        (BrowserKind.Brave, "Brave", "brave.exe", new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" }),
        (BrowserKind.Chromium, "Chromium", "chrome.exe", new[] { @"Chromium\Application\chrome.exe" }),
    };

    public static IReadOnlyList<BrowserInstall> FindAll()
    {
        var result = new List<BrowserInstall>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var (kind, name, exe, relative) in Known)
        {
            string? found = null;
            if (kind != BrowserKind.Chromium) found = FromAppPaths(exe, kind);
            found ??= roots.SelectMany(r => relative.Select(rel => Path.Combine(r, rel))).FirstOrDefault(File.Exists);
            if (found != null && result.All(r => !string.Equals(r.Path, found, StringComparison.OrdinalIgnoreCase)))
                result.Add(new BrowserInstall(kind, name, found, Version(found)));
        }
        return result;
    }

    public static BrowserInstall? Resolve(BrowserSettings s)
    {
        if (s.Preferred == BrowserKind.Custom && !string.IsNullOrWhiteSpace(s.CustomPath) && File.Exists(s.CustomPath))
            return new BrowserInstall(BrowserKind.Custom, Path.GetFileNameWithoutExtension(s.CustomPath), s.CustomPath, Version(s.CustomPath));
        var all = FindAll();
        if (s.Preferred is not (BrowserKind.Auto or BrowserKind.Custom))
            return all.FirstOrDefault(b => b.Kind == s.Preferred) ?? all.FirstOrDefault();
        // Auto: prefer Chrome (most common on claude.ai), then Edge (always present on Windows 11).
        return all.FirstOrDefault(b => b.Kind == BrowserKind.Chrome) ?? all.FirstOrDefault(b => b.Kind == BrowserKind.Edge) ?? all.FirstOrDefault();
    }

    private static string? FromAppPaths(string exe, BrowserKind kind)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (k?.GetValue(null) is string p)
                {
                    p = p.Trim('"');
                    if (File.Exists(p) && (kind != BrowserKind.Chrome || p.Contains(@"Google\Chrome", StringComparison.OrdinalIgnoreCase))) return p;
                }
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static string? Version(string path)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch { return null; }
    }
}
