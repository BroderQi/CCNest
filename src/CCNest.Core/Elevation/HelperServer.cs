using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using CCNest.Core.Infrastructure;
using CCNest.Core.Mihomo;
using CCNest.Core.Native;
using CCNest.Core.Settings;
using CCNest.Core.Wfp;

namespace CCNest.Core.Elevation;

/// <summary>
/// The elevated helper: a short-lived process started through UAC only when TUN mode or WFP
/// enforcement is needed. It accepts a fixed set of structured commands over a named pipe that only
/// the launching user can open, and only from the parent CCNest process. It never executes
/// arbitrary commands. It also acts as the crash watchdog for elevated resources: when the parent
/// disappears without saying goodbye, it applies the user's crash policy.
/// </summary>
public sealed class HelperServer
{
    private readonly int _parentPid;
    private readonly string _pipeName;
    private readonly SecurityIdentifier _clientSid;
    private CrashPolicy _policy;
    private volatile bool _shutdownRequested;
    private WfpEngine? _dynamicEngine;
    private KillSwitchRequest? _activeKillSwitch;
    private MihomoSidecar? _tunCore;
    private readonly object _lock = new();

    public HelperServer(int parentPid, string pipeName, string clientSid, CrashPolicy policy)
    {
        _parentPid = parentPid;
        _pipeName = pipeName;
        _clientSid = new SecurityIdentifier(clientSid);
        _policy = policy;
    }

    public async Task<int> RunAsync()
    {
        Log.ProcessTag = "helper";
        Log.Info("Helper", $"提权助手启动，父进程 {_parentPid}，策略 {_policy}");
        Process parent;
        try { parent = Process.GetProcessById(_parentPid); }
        catch
        {
            Log.Error("Helper", "父进程不存在，退出");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        var parentExit = parent.WaitForExitAsync(cts.Token);
        var serve = ServeLoopAsync(cts.Token);
        var finished = await Task.WhenAny(parentExit, serve).ConfigureAwait(false);

        if (finished == parentExit && !_shutdownRequested)
        {
            Log.Warn("Helper", "CCNest 主程序意外退出，执行崩溃策略");
            await ApplyCrashPolicyAsync().ConfigureAwait(false);
        }
        cts.Cancel();
        try { await serve.ConfigureAwait(false); } catch { /* cancelled */ }
        _dynamicEngine?.Dispose();
        return 0;
    }

    private async Task ApplyCrashPolicyAsync()
    {
        try
        {
            if (_policy == CrashPolicy.AutoRestore)
            {
                await StopTunCoreAsync().ConfigureAwait(false);
                RemoveKillSwitch();
                Log.Info("Helper", "已按“可用性优先”策略撤销网络阻断");
                return;
            }

            // Safety first: stop the TUN core and leave only a lockdown rule set in place, which survives
            // this process (non-dynamic WFP session) until the user restores from CCNest or reboots.
            var hadKillSwitch = _activeKillSwitch != null;
            var mode = _activeKillSwitch?.Mode;
            await StopTunCoreAsync().ConfigureAwait(false);
            if (hadKillSwitch && mode == KillSwitchMode.SystemWide)
            {
                var plan = new KillSwitchPlan { Mode = KillSwitchMode.Lockdown, AllowLan = _activeKillSwitch!.AllowLan };
                _dynamicEngine?.Dispose();
                _dynamicEngine = null;
                using var engine = KillSwitch.Apply(plan, dynamicSession: false, out var _count);
                Log.Info("Helper", "已切换为锁定规则：除本机回环/DHCP/局域网外全部阻断");
            }
            else if (hadKillSwitch && _activeKillSwitch!.Dynamic)
            {
                // Re-apply the same app-scoped rules non-dynamically so they outlive the helper.
                var plan = ToPlan(_activeKillSwitch);
                _dynamicEngine?.Dispose();
                _dynamicEngine = null;
                using var engine = KillSwitch.Apply(plan, dynamicSession: false, out var _count);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Helper", "执行崩溃策略失败", ex);
        }
    }

    private async Task ServeLoopAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(_clientSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        while (!ct.IsCancellationRequested && !_shutdownRequested)
        {
            await using var pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64 * 1024, 64 * 1024, security);
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientPid) || clientPid != (uint)_parentPid)
            {
                Log.Warn("Helper", $"拒绝来自进程 {clientPid} 的连接（仅接受 CCNest 主程序）");
                pipe.Disconnect();
                continue;
            }

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                catch (IOException) { break; }
                if (line == null) break;
                if (line.Length > 4 * 1024 * 1024) break;

                HelperResponse response;
                HelperRequest? request = null;
                try
                {
                    request = HelperJson.Deserialize<HelperRequest>(line);
                    if (request == null) throw new InvalidDataException("空请求");
                    response = new HelperResponse { Id = request.Id, Ok = true, Payload = await HandleAsync(request).ConfigureAwait(false) };
                }
                catch (Exception ex)
                {
                    Log.Error("Helper", $"命令 {request?.Command} 失败", ex);
                    response = new HelperResponse { Id = request?.Id ?? 0, Ok = false, Error = ex.Message };
                }
                await writer.WriteLineAsync(HelperJson.Serialize(response)).ConfigureAwait(false);
                if (_shutdownRequested) return;
            }
        }
    }

    private async Task<JsonNode?> HandleAsync(HelperRequest r)
    {
        switch (r.Command)
        {
            case HelperCommands.Hello:
                return new JsonObject { ["version"] = AppInfo.Version, ["pid"] = Environment.ProcessId };

            case HelperCommands.SetCrashPolicy:
                _policy = HelperJson.FromNode<CrashPolicyRequest>(r.Payload)!.Policy;
                return null;

            case HelperCommands.ApplyKillSwitch:
            {
                var req = HelperJson.FromNode<KillSwitchRequest>(r.Payload) ?? throw new InvalidDataException("缺少参数");
                var plan = ToPlan(req);
                lock (_lock)
                {
                    _dynamicEngine?.Dispose();
                    _dynamicEngine = null;
                    var engine = KillSwitch.Apply(plan, req.Dynamic, out var count);
                    if (req.Dynamic) _dynamicEngine = engine;
                    else engine.Dispose();
                    _activeKillSwitch = req;
                    return new JsonObject { ["filters"] = count };
                }
            }

            case HelperCommands.RemoveKillSwitch:
                return new JsonObject { ["removed"] = RemoveKillSwitch() };

            case HelperCommands.KillSwitchStatus:
                return new JsonObject { ["filters"] = KillSwitch.CountActiveFilters() };

            case HelperCommands.StartTunCore:
            {
                var req = HelperJson.FromNode<StartTunCoreRequest>(r.Payload) ?? throw new InvalidDataException("缺少参数");
                return await StartTunCoreAsync(req).ConfigureAwait(false);
            }

            case HelperCommands.StopTunCore:
                await StopTunCoreAsync().ConfigureAwait(false);
                return null;

            case HelperCommands.TunCoreStatus:
                return new JsonObject { ["running"] = _tunCore?.IsRunning ?? false, ["pid"] = _tunCore?.ProcessId };

            case HelperCommands.Shutdown:
            {
                var restore = r.Payload?["restore"]?.GetValue<bool>() ?? true;
                if (restore)
                {
                    await StopTunCoreAsync().ConfigureAwait(false);
                    RemoveKillSwitch();
                }
                _shutdownRequested = true;
                return null;
            }

            default:
                throw new InvalidOperationException($"不支持的命令：{r.Command}");
        }
    }

    private KillSwitchPlan ToPlan(KillSwitchRequest req)
    {
        var allowed = new List<string>(req.AllowedAppPaths.Where(File.Exists));
        foreach (var pid in req.AllowedProcessIds)
        {
            var path = IpHelper.GetProcessPath(pid);
            if (path != null) allowed.Add(path);
            else Log.Warn("Helper", $"无法解析进程 {pid} 的路径，未加入放行列表");
        }
        if (req.AllowElevatedCore) allowed.Add(Path.Combine(AppPaths.ElevatedCoreDir, "mihomo.exe"));

        ulong? luid = null;
        if (req.TunAlias != null)
        {
            luid = WfpEngine.LuidForAlias(req.TunAlias);
            if (luid == null) throw new InvalidOperationException($"找不到虚拟网卡 {req.TunAlias}");
        }

        return new KillSwitchPlan
        {
            Mode = req.Mode,
            AllowedAppPaths = allowed,
            ScopedAppPaths = req.ScopedAppPaths.Where(File.Exists).ToList(),
            TunInterfaceLuid = luid,
            AllowLan = req.AllowLan,
            BlockedLoopbackPorts = req.BlockedLoopbackPorts,
        };
    }

    private int RemoveKillSwitch()
    {
        lock (_lock)
        {
            _dynamicEngine?.Dispose();
            _dynamicEngine = null;
            _activeKillSwitch = null;
            return KillSwitch.RemoveAll();
        }
    }

    private async Task<JsonNode> StartTunCoreAsync(StartTunCoreRequest req)
    {
        await StopTunCoreAsync().ConfigureAwait(false);
        if (WfpEngine.LuidForAlias(req.TunAlias) != null)
            Log.Warn("Helper", $"虚拟网卡 {req.TunAlias} 已存在（可能是上次残留），内核将尝试接管");

        ValidateTunConfig(req.Config);
        var binary = MihomoBinary.PrepareElevatedCopy(req.SourceBinary);
        var home = Path.Combine(AppPaths.ElevatedCoreDir, "home");
        _tunCore = new MihomoSidecar(binary, home);
        _tunCore.UnexpectedExit += (code, output) => Log.Error("Helper", $"TUN 内核意外退出（{code}）：{output}");

        var json = JsonNode.Parse(req.Config)!.AsObject();
        var controller = json["external-controller"]!.GetValue<string>();
        var port = int.Parse(controller[(controller.LastIndexOf(':') + 1)..]);
        var ports = new SidecarPorts(json["mixed-port"]!.GetValue<int>(), 0, null, port, json["secret"]!.GetValue<string>());
        await _tunCore.StartAsync(req.Config, ports).ConfigureAwait(false);

        // Wait for the TUN adapter to appear.
        ulong? luid = null;
        for (var i = 0; i < 60 && luid == null; i++)
        {
            luid = WfpEngine.LuidForAlias(req.TunAlias);
            if (luid == null) await Task.Delay(250).ConfigureAwait(false);
        }
        if (luid == null)
        {
            var output = _tunCore.RecentOutput;
            await StopTunCoreAsync().ConfigureAwait(false);
            throw new InvalidOperationException("TUN 虚拟网卡未能创建：" + output.Split('\n').LastOrDefault());
        }
        return new JsonObject { ["pid"] = _tunCore.ProcessId, ["luid"] = luid.Value };
    }

    /// <summary>Defence in depth: the elevated core only accepts configs that keep its controller on loopback.</summary>
    internal static void ValidateTunConfig(string config)
    {
        var json = JsonNode.Parse(config) as JsonObject ?? throw new InvalidDataException("配置必须是 JSON 对象");
        var ctl = json["external-controller"]?.GetValue<string>() ?? throw new InvalidDataException("缺少控制接口");
        if (!ctl.StartsWith("127.0.0.1:", StringComparison.Ordinal)) throw new InvalidDataException("控制接口必须绑定 127.0.0.1");
        if ((json["secret"]?.GetValue<string>()?.Length ?? 0) < 32) throw new InvalidDataException("控制接口密钥过短");
        if (json["allow-lan"]?.GetValue<bool>() == true) throw new InvalidDataException("不允许 allow-lan");
        foreach (var forbidden in new[] { "external-ui", "external-ui-url", "external-controller-unix", "external-controller-pipe", "external-controller-tls", "tunnels" })
            if (json.ContainsKey(forbidden)) throw new InvalidDataException($"不允许的配置项：{forbidden}");
    }

    private async Task StopTunCoreAsync()
    {
        var core = _tunCore;
        _tunCore = null;
        if (core != null) await core.DisposeAsync().ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
