using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CCNest.Core.Infrastructure;
using CCNest.Core.Native;

namespace CCNest.Core.Claude;

public sealed record ClaudeCodeInfo
{
    public bool Installed { get; init; }
    public string? ExecutablePath { get; init; }
    public string? InstallKind { get; init; }
    public string? Version { get; init; }
    public string SettingsPath { get; init; } = "";
    public bool SettingsExists { get; init; }
    public string? SettingsError { get; init; }
    public IReadOnlyDictionary<string, string> SettingsProxyEnv { get; init; } = new Dictionary<string, string>();
    public string? ManagedSettingsPath { get; init; }
    public IReadOnlyDictionary<string, string> ManagedProxyEnv { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<int> RunningPids { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Claude Code (CLI) integration. Claude Code honours HTTPS_PROXY/HTTP_PROXY/NO_PROXY (HTTP proxies
/// only, no SOCKS) and reads them from the "env" block of ~/.claude/settings.json, which also applies
/// to Claude Desktop's Code tab. CCNest merges only these keys and restores the original values;
/// all other settings are preserved byte-for-byte in meaning.
/// </summary>
public static class ClaudeCodeManager
{
    public static readonly string[] ManagedKeys = { "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY" };

    public const string DefaultNoProxy = "localhost,127.0.0.1,::1";

    /// <summary>Tests can redirect the settings file.</summary>
    public static string? SettingsPathOverride { get; set; }

    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppPaths.UserProfile, ".claude");

    public static string SettingsPath => SettingsPathOverride ?? Path.Combine(ConfigDir, "settings.json");

    public static string ManagedSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json");

    public static async Task<ClaudeCodeInfo> DetectAsync(bool queryVersion = true, CancellationToken ct = default)
    {
        var exe = FindExecutable(out var kind);
        string? version = null;
        if (exe != null && queryVersion)
        {
            try
            {
                var r = await ProcessRunner.RunAsync(exe, new[] { "--version" }, TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
                if (r.Success) version = r.StdOut.Trim().Split('\n')[0].Trim();
            }
            catch (Exception ex)
            {
                Log.Debug("ClaudeCode", $"无法获取版本：{ex.Message}");
            }
        }

        var settingsPath = SettingsPath;
        string? error = null;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(settingsPath))
        {
            try
            {
                var root = ReadSettings(settingsPath);
                if (root["env"] is JsonObject e)
                    foreach (var (k, v) in e)
                        if (IsProxyKey(k) && v is JsonValue) env[k] = v.ToString();
            }
            catch (Exception ex)
            {
                error = $"settings.json 无法解析：{ex.Message}";
            }
        }

        var managedEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? managedPath = null;
        if (File.Exists(ManagedSettingsPath))
        {
            managedPath = ManagedSettingsPath;
            try
            {
                var m = ReadSettings(managedPath);
                if (m["env"] is JsonObject e)
                    foreach (var (k, v) in e)
                        if (IsProxyKey(k) && v is JsonValue) managedEnv[k] = v.ToString();
            }
            catch { /* ignore */ }
        }

        return new ClaudeCodeInfo
        {
            Installed = exe != null,
            ExecutablePath = exe,
            InstallKind = kind,
            Version = version,
            SettingsPath = settingsPath,
            SettingsExists = File.Exists(settingsPath),
            SettingsError = error,
            SettingsProxyEnv = env,
            ManagedSettingsPath = managedPath,
            ManagedProxyEnv = managedEnv,
            RunningPids = RunningClaudeCodePids(),
        };
    }

    public static bool IsProxyKey(string key) =>
        key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase) || key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) || key.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase);

    public static string? FindExecutable(out string? kind)
    {
        var candidates = new (string path, string kind)[]
        {
            (Path.Combine(AppPaths.UserProfile, ".local", "bin", "claude.exe"), "原生安装"),
            (Path.Combine(AppPaths.RoamingAppData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"), "npm 全局安装"),
            (Path.Combine(AppPaths.RoamingAppData, "npm", "claude.cmd"), "npm 全局安装"),
            (Path.Combine(AppPaths.LocalAppData, "Programs", "claude", "claude.exe"), "原生安装"),
        };
        foreach (var (path, k) in candidates)
        {
            if (File.Exists(path))
            {
                kind = k;
                return path;
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), name);
                    if (File.Exists(p))
                    {
                        kind = "PATH";
                        return p;
                    }
                }
                catch { /* invalid PATH entry */ }
            }
        }
        kind = null;
        return null;
    }

    /// <summary>PIDs of running Claude Code processes (claude.exe outside of the Claude Desktop install).</summary>
    public static IReadOnlyList<int> RunningClaudeCodePids()
    {
        var list = new List<int>();
        foreach (var p in SafeProcesses("claude"))
        {
            var path = IpHelper.GetProcessPath(p.Id) ?? "";
            if (path.Contains("claude-code", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\.local\bin\", StringComparison.OrdinalIgnoreCase))
                list.Add(p.Id);
        }
        return list;
    }

    internal static IEnumerable<Process> SafeProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return Array.Empty<Process>(); }
    }

    public static JsonObject ReadSettings(string path, bool strict = false)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var options = strict
            ? new JsonDocumentOptions()
            : new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: options);
        }
        catch (JsonException) when (strict)
        {
            // Lenient parse succeeds => the file has comments/trailing commas we would lose on rewrite.
            JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            throw new InvalidDataException("settings.json 含有注释或尾随逗号，为避免丢失内容 CCNest 不会自动修改它，请手动编辑");
        }
        return node as JsonObject ?? throw new InvalidDataException("settings.json 顶层不是 JSON 对象");
    }

    /// <summary>The proxy environment CCNest writes for a local mixed port.</summary>
    public static Dictionary<string, string?> ProxyEnvFor(int port, string? existingNoProxy)
    {
        var url = $"http://127.0.0.1:{port}";
        return new Dictionary<string, string?>
        {
            ["HTTPS_PROXY"] = url,
            ["HTTP_PROXY"] = url,
            ["NO_PROXY"] = MergeNoProxy(existingNoProxy),
        };
    }

    public static string MergeNoProxy(string? existing)
    {
        var items = new List<string>();
        foreach (var part in (existing ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (!items.Contains(part, StringComparer.OrdinalIgnoreCase)) items.Add(part);
        foreach (var part in DefaultNoProxy.Split(','))
            if (!items.Contains(part, StringComparer.OrdinalIgnoreCase)) items.Add(part);
        return string.Join(",", items);
    }

    /// <summary>
    /// Reads the current values of the managed keys (case-insensitive) from settings.json.
    /// Missing keys map to null.
    /// </summary>
    public static Dictionary<string, string?> ReadManagedEnv(string path)
    {
        var result = ManagedKeys.ToDictionary(k => k, _ => (string?)null);
        if (!File.Exists(path)) return result;
        var root = ReadSettings(path);
        if (root["env"] is JsonObject env)
            foreach (var key in ManagedKeys)
            {
                var actual = env.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (actual != null) result[key] = env[actual]?.ToString();
            }
        return result;
    }

    /// <summary>
    /// Sets (value != null) or removes (value == null) keys inside "env", preserving everything else.
    /// Throws (and leaves the file untouched) when it cannot be parsed losslessly.
    /// </summary>
    public static void WriteManagedEnv(string path, IReadOnlyDictionary<string, string?> values, bool removeEmptyEnv)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            root = ReadSettings(path, strict: true); // throws on invalid JSON: never clobber a file we cannot parse
            var backup = Path.Combine(AppPaths.BackupsDir, $"claude-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(path, backup, overwrite: true);
        }
        else
        {
            root = new JsonObject();
        }

        if (root["env"] is not JsonObject env)
        {
            env = new JsonObject();
            root["env"] = env;
        }

        foreach (var (key, value) in values)
        {
            foreach (var existing in env.Select(kv => kv.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
                env.Remove(existing);
            if (value != null) env[key] = value;
        }

        if (removeEmptyEnv && env.Count == 0) root.Remove("env");
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        AtomicFile.WriteAllText(path, json + Environment.NewLine);
    }

    /// <summary>Opens a new terminal window whose environment points Claude Code at CCNest.</summary>
    public static void LaunchTerminal(int port, string? workingDirectory = null)
    {
        var wt = Path.Combine(AppPaths.LocalAppData, "Microsoft", "WindowsApps", "wt.exe");
        var env = ProxyEnvFor(port, Environment.GetEnvironmentVariable("NO_PROXY"));
        var cwd = workingDirectory ?? AppPaths.UserProfile;
        var banner = "Write-Host 'CCNest: 此窗口中的 Claude Code 将通过 CCNest 专属线路联网 (HTTPS_PROXY=" + env["HTTPS_PROXY"] + ")' -ForegroundColor Green";
        ProcessStartInfo psi;
        if (File.Exists(wt))
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false, WorkingDirectory = cwd };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(cwd);
            psi.ArgumentList.Add("powershell.exe");
            psi.ArgumentList.Add("-NoExit");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(banner);
        }
        else
        {
            psi = new ProcessStartInfo(ProcessRunner.PowerShellPath) { UseShellExecute = false, WorkingDirectory = cwd };
            psi.ArgumentList.Add("-NoExit");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(banner);
        }
        foreach (var (k, v) in env) psi.Environment[k] = v;
        Process.Start(psi);
    }
}
