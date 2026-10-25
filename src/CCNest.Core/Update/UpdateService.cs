using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CCNest.Core.Infrastructure;

namespace CCNest.Core.Update;

/// <summary>How this copy of CCNest was deployed, which decides how an update is applied.</summary>
public enum InstallKind
{
    /// <summary>Installed by the Inno Setup installer (an uninstaller sits next to CCNest.exe).</summary>
    Installer,
    /// <summary>Unpacked from the portable zip.</summary>
    Portable,
}

public sealed record UpdateAsset(string Name, Uri Url, long Size);

public sealed record UpdateInfo(Version Version, string Tag, string? Notes, Uri PageUrl, DateTimeOffset? PublishedAt,
    UpdateAsset? Installer, UpdateAsset? Portable, UpdateAsset? Checksums)
{
    public string VersionText => Version.ToString(3);

    public UpdateAsset? AssetFor(InstallKind kind) => kind == InstallKind.Installer ? Installer : Portable;
}

/// <summary>
/// Self-update from GitHub Releases (published by .github/workflows/release.yml).
///
///  1. <see cref="FetchLatestAsync"/> reads the "latest" release (drafts and pre-releases are never offered).
///  2. <see cref="DownloadAsync"/> downloads the installer or portable zip and verifies it against the
///     release's SHA256SUMS.txt; a file that does not match is deleted and never run.
///  3. <see cref="StartApply"/> starts a small standard-user script that waits for CCNest to exit,
///     runs the installer silently (it asks for UAC itself) or copies the new portable files over the
///     old ones, and then starts CCNest again. User data in %LOCALAPPDATA%\CCNest is never touched.
/// </summary>
public static class UpdateService
{
    public const string Repository = "BroderQi/CCNest";
    public static string DefaultFeedUrl => $"https://api.github.com/repos/{Repository}/releases/latest";
    public static string ReleasesPageUrl => $"https://github.com/{Repository}/releases/latest";

    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    private static readonly HttpClient Http = CreateClient();

    public static string UpdatesDir => Path.Combine(AppPaths.Root, "updates");

    /// <summary>Configured feed, or the official BroderQi/CCNest releases when none is set.</summary>
    public static string ResolveFeed(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultFeedUrl : configured.Trim();

    public static InstallKind CurrentInstallKind
    {
        get
        {
            try
            {
                return Directory.EnumerateFiles(AppPaths.InstallDir, "unins*.exe").Any() ? InstallKind.Installer : InstallKind.Portable;
            }
            catch
            {
                return InstallKind.Portable;
            }
        }
    }

    public static Version CurrentVersion => TryParseVersion(AppInfo.Version, out var v) ? v : new Version(0, 0, 0);

    /// <summary>A portable copy can only update itself when its folder is writable by the current user.</summary>
    public static bool CanUpdatePortableInPlace()
    {
        try
        {
            var probe = Path.Combine(AppPaths.InstallDir, $".ccnest-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<UpdateInfo> FetchLatestAsync(string feedUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("更新源必须是 https:// 地址");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode == 404) throw new InvalidOperationException("发布页上还没有正式版本");
        if ((int)response.StatusCode == 403) throw new InvalidOperationException("GitHub 暂时限制了访问频率，请稍后再试");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return ParseRelease(json);
    }

    internal static UpdateInfo ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var version)) throw new InvalidOperationException($"无法识别版本号：{tag}");

        Uri page = new(ReleasesPageUrl);
        if (root.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var p) && p.Scheme == Uri.UriSchemeHttps)
            page = p;
        DateTimeOffset? published = root.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String && pa.TryGetDateTimeOffset(out var d) ? d : null;
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                if (string.IsNullOrEmpty(name) || !IsSafeFileName(name)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var assetUri) || assetUri.Scheme != Uri.UriSchemeHttps) continue;
                assets.Add(new UpdateAsset(name, assetUri, size));
            }
        }

        var v = version.ToString(3);
        UpdateAsset? Find(string exact, Func<string, bool> fallback) =>
            assets.FirstOrDefault(a => string.Equals(a.Name, exact, StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => fallback(a.Name));

        var installer = Find($"CCNest-Setup-{v}-x64.exe",
            n => n.StartsWith("CCNest-Setup-", StringComparison.OrdinalIgnoreCase) && n.EndsWith("-x64.exe", StringComparison.OrdinalIgnoreCase));
        var portable = Find($"CCNest-{v}-win-x64-portable.zip",
            n => n.StartsWith("CCNest-", StringComparison.OrdinalIgnoreCase) && n.EndsWith("-win-x64-portable.zip", StringComparison.OrdinalIgnoreCase));
        var sums = assets.FirstOrDefault(a => string.Equals(a.Name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));
        return new UpdateInfo(version, tag, notes, page, published, installer, portable, sums);
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>Parses "&lt;sha256&gt;  &lt;file name&gt;" lines (sha256sum / build.ps1 format).</summary>
    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length < 66) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var name = line[64..].Trim().TrimStart('*');
            if (name.Length > 0) result[name] = hash.ToLowerInvariant();
        }
        return result;
    }

    /// <summary>Downloads the package for <paramref name="kind"/> and returns its path once its SHA-256 matches.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, InstallKind kind, IProgress<double>? progress, CancellationToken ct = default)
    {
        var asset = info.AssetFor(kind) ?? throw new InvalidOperationException(
            kind == InstallKind.Installer ? "该版本没有提供安装包" : "该版本没有提供便携版压缩包");
        if (info.Checksums == null) throw new InvalidOperationException("该版本缺少 SHA256SUMS.txt，出于安全考虑不会自动安装");

        string sumsText;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            sumsText = await Http.GetStringAsync(info.Checksums.Url, timeout.Token).ConfigureAwait(false);
        }
        if (!ParseChecksums(sumsText).TryGetValue(asset.Name, out var expected))
            throw new InvalidOperationException($"SHA256SUMS.txt 中没有 {asset.Name} 的校验值");

        var dir = Directory.CreateDirectory(Path.Combine(UpdatesDir, info.VersionText)).FullName;
        var target = Path.Combine(dir, asset.Name);
        if (File.Exists(target))
        {
            if (await HashFileAsync(target, ct).ConfigureAwait(false) == expected)
            {
                progress?.Report(1);
                return target;
            }
            File.Delete(target);
        }

        var partial = target + ".part";
        try
        {
            using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != expected)
                throw new InvalidOperationException($"{asset.Name} 的 SHA-256 与发布页不一致，已丢弃下载文件");
            File.Move(partial, target, overwrite: true);
            progress?.Report(1);
            Log.Info("Update", $"已下载并校验 {asset.Name}（{expected}）");
            return target;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* ignore */ }
        }
    }

    /// <summary>Unpacks a verified portable zip next to it and returns the folder holding the new CCNest.exe.</summary>
    public static string ExtractPortable(string zipPath)
    {
        var staging = Path.Combine(Path.GetDirectoryName(zipPath)!, "portable");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging);
        if (File.Exists(Path.Combine(staging, "CCNest.exe"))) return staging;
        // Tolerate a zip that wraps everything in a single top-level folder.
        var nested = Directory.GetDirectories(staging).FirstOrDefault(d => File.Exists(Path.Combine(d, "CCNest.exe")));
        return nested ?? throw new InvalidOperationException("便携版压缩包中没有 CCNest.exe");
    }

    /// <summary>
    /// Starts the apply script (standard user, hidden window). The caller must exit right afterwards:
    /// the script waits for this process to end before it replaces any file.
    /// </summary>
    public static void StartApply(InstallKind kind, string source)
    {
        var dir = Directory.CreateDirectory(UpdatesDir).FullName;
        var script = Path.Combine(dir, "apply-update.ps1");
        File.WriteAllText(script, ApplyScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var log = Path.Combine(AppPaths.LogsDir, $"update-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(File.Exists(powershell) ? powershell : "powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                     "-WaitPid", Environment.ProcessId.ToString(),
                     "-Mode", kind.ToString(),
                     "-Source", TrimSeparators(source),
                     "-Target", TrimSeparators(AppPaths.InstallDir),
                     "-LogFile", log,
                 })
            psi.ArgumentList.Add(arg);
        using var _ = Process.Start(psi) ?? throw new InvalidOperationException("无法启动更新程序");
        Log.Info("Update", $"已启动更新（{kind}）：{source}，日志 {log}");
    }

    /// <summary>Removes downloads of versions other than <paramref name="keepVersion"/>.</summary>
    public static void CleanupDownloads(string? keepVersion = null)
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (var d in Directory.GetDirectories(UpdatesDir))
                if (!string.Equals(Path.GetFileName(d), keepVersion, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(d, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug("Update", "清理旧的更新文件失败：" + ex.Message);
        }
    }

    private static string TrimSeparators(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSafeFileName(string name) =>
        name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static HttpClient CreateClient()
    {
        // Default handler: follows the system proxy, so checks work through the user's existing proxy setup.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CCNest/{AppInfo.Version} (+https://github.com/{Repository})");
        return client;
    }

    /// <summary>
    /// Runs as the standard user after CCNest exits. ASCII only (Windows PowerShell 5.1).
    /// Installer: runs the verified setup silently; Inno Setup shows its own UAC prompt and keeps the
    /// existing install folder. Portable: copies the verified files over the old ones. Either way CCNest
    /// is started again afterwards (with --updated or --update-failed).
    /// </summary>
    internal const string ApplyScript = """
param(
    [Parameter(Mandatory = $true)][int]$WaitPid,
    [Parameter(Mandatory = $true)][ValidateSet('Installer', 'Portable')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$LogFile
)
$ErrorActionPreference = 'Stop'
function Write-Log([string]$Message) {
    try { Add-Content -LiteralPath $LogFile -Value ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message) -Encoding UTF8 } catch { }
}

$exe = Join-Path $Target 'CCNest.exe'
$ok = $false
try {
    Write-Log "CCNest update ($Mode): waiting for process $WaitPid to exit"
    $parent = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
    if ($parent -and -not $parent.WaitForExit(120000)) { throw 'CCNest did not exit within 120 seconds' }

    # The helper / watchdog processes stop shortly after the session ends.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name 'CCNest' -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 500
    }

    if ($Mode -eq 'Installer') {
        $setupLog = [IO.Path]::ChangeExtension($LogFile, '.setup.log')
        $arguments = @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS', ('/LOG="{0}"' -f $setupLog))
        Write-Log "running installer $Source"
        $setup = Start-Process -FilePath $Source -ArgumentList $arguments -PassThru
        $null = $setup.Handle
        $setup.WaitForExit()
        Write-Log "installer exit code $($setup.ExitCode)"
        $ok = ($setup.ExitCode -eq 0)
    } else {
        Write-Log "copying $Source -> $Target"
        & robocopy.exe $Source $Target /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        $code = $LASTEXITCODE
        Write-Log "robocopy exit code $code"
        $ok = ($code -lt 8)
    }
} catch {
    Write-Log "update failed: $($_.Exception.Message)"
}

try {
    if (Test-Path -LiteralPath $exe) {
        if ($ok) { Start-Process -FilePath $exe -ArgumentList '--updated' }
        else { Start-Process -FilePath $exe -ArgumentList '--update-failed' }
    } else {
        Write-Log "CCNest.exe not found at $exe"
    }
} catch {
    Write-Log "relaunch failed: $($_.Exception.Message)"
}
""";
}
