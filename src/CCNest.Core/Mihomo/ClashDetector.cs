using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CCNest.Core.Infrastructure;
using CCNest.Core.Native;
using CCNest.Core.Proxy;

namespace CCNest.Core.Mihomo;

public sealed record DetectedPort(int Port, bool Socks5, bool Http, bool LoopbackOnly);

public sealed record ClashGroupInfo(string Name, string Type, string? Now, IReadOnlyList<string> Members);

/// <summary>Result of detecting an existing Clash/mihomo environment.</summary>
public sealed record ClashEnvironment
{
    public string ClientName { get; init; } = "未检测到";
    public bool ClientRunning { get; init; }
    public string? CoreProcessName { get; init; }
    public int? CorePid { get; init; }
    public string? CorePath { get; init; }
    public bool CoreRunsElevatedOrService { get; init; }
    public IReadOnlyList<DetectedPort> Ports { get; init; } = Array.Empty<DetectedPort>();
    public string? ControllerAddress { get; init; }
    public string? ControllerSecret { get; init; }
    public bool ControllerReachable { get; init; }
    public string? ControllerError { get; init; }
    public string? CoreVersion { get; init; }
    public bool? TunEnabled { get; init; }
    public string? Mode { get; init; }
    public bool? ClientSystemProxyEnabled { get; init; }
    public bool? ClientProxyGuardEnabled { get; init; }
    public bool? ClientTunSettingEnabled { get; init; }
    public IReadOnlyList<ClashGroupInfo> Groups { get; init; } = Array.Empty<ClashGroupInfo>();
    public IReadOnlyList<string> ConfigFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool Found => ClientRunning || Ports.Count > 0 || ControllerReachable;

    /// <summary>Recommended port for chaining (prefer a SOCKS5-capable mixed port).</summary>
    public DetectedPort? RecommendedPort => Ports.FirstOrDefault(p => p.Socks5 && p.Http) ?? Ports.FirstOrDefault(p => p.Socks5) ?? Ports.FirstOrDefault(p => p.Http);
}

/// <summary>
/// Detects an existing Clash-compatible client (Clash Verge Rev, mihomo, Clash for Windows, FlClash,
/// Clash Nyanpasu, v2rayN, sing-box) by process, listening ports, configuration files and controller.
/// Detection is strictly read-only.
/// </summary>
public static class ClashDetector
{
    private sealed record KnownClient(string Display, string[] GuiProcesses, string[] CoreProcesses);

    private static readonly KnownClient[] KnownClients =
    {
        new("Clash Verge Rev", new[] { "clash-verge.exe", "clash-verge-service.exe" }, new[] { "verge-mihomo.exe", "verge-mihomo-alpha.exe" }),
        new("Clash Nyanpasu", new[] { "clash-nyanpasu.exe", "nyanpasu-service.exe" }, new[] { "mihomo.exe", "mihomo-alpha.exe", "clash-rs.exe" }),
        new("FlClash", new[] { "FlClash.exe" }, new[] { "FlClashCore.exe" }),
        new("Clash for Windows", new[] { "Clash for Windows.exe" }, new[] { "clash-win64.exe", "clash.exe" }),
        new("v2rayN", new[] { "v2rayN.exe" }, new[] { "mihomo.exe", "xray.exe", "sing-box.exe" }),
        new("mihomo", Array.Empty<string>(), new[] { "mihomo.exe", "mihomo-windows-amd64.exe", "clash-meta.exe", "Clash.Meta-windows-amd64.exe" }),
        new("sing-box", Array.Empty<string>(), new[] { "sing-box.exe" }),
        new("Hiddify", new[] { "Hiddify.exe" }, new[] { "HiddifyCli.exe" }),
    };

    public static string VergeDataDir => Path.Combine(AppPaths.RoamingAppData, "io.github.clash-verge-rev.clash-verge-rev");

    public static IEnumerable<string> CandidateConfigFiles()
    {
        yield return Path.Combine(VergeDataDir, "clash-verge.yaml"); // runtime config (authoritative)
        yield return Path.Combine(VergeDataDir, "config.yaml");
        yield return Path.Combine(AppPaths.UserProfile, ".config", "mihomo", "config.yaml");
        yield return Path.Combine(AppPaths.UserProfile, ".config", "clash", "config.yaml");
        yield return Path.Combine(AppPaths.RoamingAppData, "clash-nyanpasu", "clash-config.yaml");
    }

    public static async Task<ClashEnvironment> DetectAsync(string? controllerOverride = null, string? secretOverride = null, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var processes = SafeProcessList();
        var ownMihomo = Path.GetFullPath(AppPaths.BundledMihomoPath);

        KnownClient? client = null;
        Process? core = null;
        foreach (var known in KnownClients)
        {
            var gui = processes.FirstOrDefault(p => known.GuiProcesses.Contains(p.ProcessName + ".exe", StringComparer.OrdinalIgnoreCase));
            var c = processes.FirstOrDefault(p => known.CoreProcesses.Contains(p.ProcessName + ".exe", StringComparer.OrdinalIgnoreCase)
                                                   && !IsOwnCore(p, ownMihomo));
            if (gui != null || c != null)
            {
                client = known;
                core = c;
                if (c != null) break;
            }
        }

        string? corePath = core != null ? IpHelper.GetProcessPath(core.Id) : null;
        var coreElevated = core != null && corePath == null;

        // Configuration files
        var files = new List<string>();
        ClashFileConfig? fileCfg = null;
        foreach (var path in CandidateConfigFiles())
        {
            var cfg = ClashConfigReader.TryRead(path);
            if (cfg == null) continue;
            files.Add(path);
            fileCfg ??= cfg;
        }

        // Verge-specific GUI settings
        bool? vergeSysProxy = null, vergeGuard = null, vergeTun = null;
        var vergeYaml = Path.Combine(VergeDataDir, "verge.yaml");
        if (File.Exists(vergeYaml))
        {
            try
            {
                var text = await File.ReadAllTextAsync(vergeYaml, ct).ConfigureAwait(false);
                vergeSysProxy = ReadYamlBool(text, "enable_system_proxy");
                vergeGuard = ReadYamlBool(text, "enable_proxy_guard");
                vergeTun = ReadYamlBool(text, "enable_tun_mode");
                files.Add(vergeYaml);
            }
            catch (Exception ex)
            {
                Log.Debug("ClashDetector", $"verge.yaml 读取失败: {ex.Message}");
            }
        }

        // Listening ports owned by the core
        var ports = new List<DetectedPort>();
        var listeners = SafeListeners();
        var corePids = new HashSet<int>();
        if (core != null) corePids.Add(core.Id);
        var candidatePorts = listeners.Where(l => corePids.Contains(l.Pid)).Select(l => (l.Port, l.Address)).ToList();
        foreach (var p in new[] { fileCfg?.MixedPort, fileCfg?.SocksPort, fileCfg?.Port })
            if (p is { } port && candidatePorts.All(c => c.Port != port) && listeners.Any(l => l.Port == port))
                candidatePorts.Add((port, IPAddress.Loopback));

        foreach (var group in candidatePorts.GroupBy(c => c.Port))
        {
            var loopbackOnly = group.All(g => IPAddress.IsLoopback(g.Address));
            var socks = await ProbeSocks5Async(group.Key, ct).ConfigureAwait(false);
            var http = await ProbeHttpAsync(group.Key, ct).ConfigureAwait(false);
            if (socks || http) ports.Add(new DetectedPort(group.Key, socks, http, loopbackOnly));
        }
        // Prefer configured mixed port order
        ports = ports.OrderByDescending(p => p.Port == fileCfg?.MixedPort).ThenByDescending(p => p.Socks5 && p.Http).ToList();

        // Controller
        var candidates = new List<(string addr, string? secret)>();
        if (!string.IsNullOrWhiteSpace(controllerOverride)) candidates.Add((controllerOverride!, secretOverride ?? fileCfg?.Secret));
        if (fileCfg?.ExternalControllerPipe is { } pipe) candidates.Add((pipe, fileCfg.Secret));
        if (fileCfg?.ExternalController is { } tcp) candidates.Add((tcp, fileCfg.Secret));
        foreach (var l in listeners.Where(l => corePids.Contains(l.Pid) && ports.All(p => p.Port != l.Port)))
            candidates.Add(($"127.0.0.1:{l.Port}", fileCfg?.Secret));
        candidates.Add((@"\\.\pipe\verge-mihomo", fileCfg?.Secret));

        string? ctlAddr = null, ctlSecret = null, ctlError = null, version = null, mode = fileCfg?.Mode;
        bool reachable = false;
        bool? tun = vergeTun ?? fileCfg?.TunEnabled;
        var groups = new List<ClashGroupInfo>();
        foreach (var (addr, secret) in candidates.DistinctBy(c => c.addr.ToLowerInvariant()))
        {
            if (MihomoController.IsPipeAddress(addr) && !PipeExists(addr)) continue;
            try
            {
                using var ctl = new MihomoController(addr, secret, TimeSpan.FromSeconds(3));
                var v = await ctl.GetVersionAsync(ct).ConfigureAwait(false);
                ctlAddr = addr;
                ctlSecret = secret;
                version = v.Version + (v.Meta ? " (Meta)" : "");
                reachable = true;
                ctlError = null;
                var cfg = await ctl.GetConfigsAsync(ct).ConfigureAwait(false);
                mode = cfg["mode"]?.GetValue<string>() ?? mode;
                if (cfg["tun"] is System.Text.Json.Nodes.JsonObject t && t["enable"] is { } en) tun = en.GetValue<bool>();
                var proxies = await ctl.GetProxiesAsync(ct).ConfigureAwait(false);
                foreach (var p in proxies.Values.Where(p => p.Type is "Selector" or "URLTest" or "Fallback" or "LoadBalance"))
                    if (p.Name != "GLOBAL" || proxies.Count < 50) groups.Add(new ClashGroupInfo(p.Name, p.Type, p.Now, p.All));
                break;
            }
            catch (MihomoApiException ex)
            {
                if (ex.Unauthorized)
                {
                    ctlAddr = addr;
                    ctlError = "找到控制接口，但密钥（secret）不正确，请在设置中手动填写";
                    break;
                }
                ctlError ??= ex.Message;
            }
            catch (Exception ex)
            {
                ctlError ??= ex.Message;
            }
        }

        if (client == null && ports.Count == 0 && !reachable)
            notes.Add("未检测到正在运行的 Clash / Mihomo 客户端。可以手动填写一级代理，或选择“直连二级代理”。");
        if (coreElevated)
            notes.Add($"{core!.ProcessName} 以服务/管理员权限运行（例如 Clash Verge 服务模式），CCNest 无法读取其路径，但可以正常使用其本地端口。");
        if (tun == true)
            notes.Add("现有客户端已开启 TUN（虚拟网卡）模式。CCNest 的标准模式可以与其共存；若使用 CCNest 的全局 TUN 模式，需要暂时关闭它。");
        if (vergeGuard == true)
            notes.Add("Clash Verge 已开启“系统代理守卫”，它会把系统代理改回 Clash 端口，与 CCNest 的系统代理设置冲突。");

        return new ClashEnvironment
        {
            ClientName = client?.Display ?? (ports.Count > 0 ? "未知 Clash 兼容客户端" : "未检测到"),
            ClientRunning = client != null,
            CoreProcessName = core != null ? core.ProcessName + ".exe" : null,
            CorePid = core?.Id,
            CorePath = corePath,
            CoreRunsElevatedOrService = coreElevated,
            Ports = ports,
            ControllerAddress = ctlAddr,
            ControllerSecret = ctlSecret,
            ControllerReachable = reachable,
            ControllerError = reachable ? null : ctlError,
            CoreVersion = version,
            TunEnabled = tun,
            Mode = mode,
            ClientSystemProxyEnabled = vergeSysProxy,
            ClientProxyGuardEnabled = vergeGuard,
            ClientTunSettingEnabled = vergeTun,
            Groups = groups,
            ConfigFiles = files,
            Notes = notes,
        };
    }

    private static bool IsOwnCore(Process p, string ownPath)
    {
        var path = IpHelper.GetProcessPath(p.Id);
        return path != null && (string.Equals(Path.GetFullPath(path), ownPath, StringComparison.OrdinalIgnoreCase)
                                || path.Contains(@"\CCNest\", StringComparison.OrdinalIgnoreCase));
    }

    private static List<Process> SafeProcessList()
    {
        try { return Process.GetProcesses().ToList(); }
        catch { return new List<Process>(); }
    }

    private static IReadOnlyList<Native.TcpListener> SafeListeners()
    {
        try { return IpHelper.GetTcpListeners(); }
        catch { return Array.Empty<Native.TcpListener>(); }
    }

    public static bool PipeExists(string address)
    {
        try { return File.Exists(address.Replace('/', '\\')) || Directory.GetFiles(@"\\.\pipe\").Any(f => f.Equals(address, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private static bool? ReadYamlBool(string yaml, string key)
    {
        foreach (var line in yaml.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (!t.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var v = t[(key.Length + 1)..].Trim();
            return bool.TryParse(v, out var b) ? b : null;
        }
        return null;
    }

    public static async Task<bool> ProbeSocks5Async(int port, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            await socket.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, SocketFlags.None, cts.Token).ConfigureAwait(false);
            var buf = new byte[2];
            var n = await socket.ReceiveAsync(buf, SocketFlags.None, cts.Token).ConfigureAwait(false);
            return n == 2 && buf[0] == 0x05 && buf[1] is 0x00 or 0x02;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> ProbeHttpAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            // An origin-form request: HTTP proxies answer it locally (typically 400) without contacting anything external.
            var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
            await socket.SendAsync(req, SocketFlags.None, cts.Token).ConfigureAwait(false);
            var buf = new byte[16];
            var n = await socket.ReceiveAsync(buf, SocketFlags.None, cts.Token).ConfigureAwait(false);
            return n >= 8 && Encoding.ASCII.GetString(buf, 0, n).StartsWith("HTTP/1.", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Builds the first-stage endpoint for chaining through the detected client.</summary>
    public static ProxyEndpoint EndpointFor(DetectedPort port) => new()
    {
        Protocol = port.Socks5 ? ProxyProtocol.Socks5 : ProxyProtocol.Http,
        Host = "127.0.0.1",
        Port = port.Port,
    };
}
