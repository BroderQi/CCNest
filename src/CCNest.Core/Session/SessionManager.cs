using System.Text.Json.Nodes;
using CCNest.Core.Claude;
using CCNest.Core.Elevation;
using CCNest.Core.Infrastructure;
using CCNest.Core.Mihomo;
using CCNest.Core.Proxy;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;
using CCNest.Core.Wfp;

namespace CCNest.Core.Session;

/// <summary>
/// Orchestrates the environment session: activation (inspect → snapshot → apply → verify → open),
/// monitoring (core health, chain health, exit IP, external modifications, sleep/wake), and
/// restoration. Every modification is journaled before it is made.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private SessionJournal? _journal;
    private MihomoSidecar? _sidecar;
    private HelperClient? _helper;
    private MihomoController? _controller;
    private SidecarPorts? _ports;
    private AppSettings? _activeSettings;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private SessionStatusInfo _status = new();
    private int _failures;
    private int _rotation;
    private DateTime _lastExitCheck = DateTime.MinValue;
    private string? _verifiedIp;
    private readonly List<string> _warnings = new();
    private System.Diagnostics.Process? _watchdog;
    private volatile bool _reverifyRequested;
    private DateTime _activatedAt = DateTime.MinValue;

    public SessionManager(SettingsStore settings) => _settings = settings;

    /// <summary>URL used for the periodic end-to-end health check through the chain (test hook: settable).</summary>
    internal static string HealthCheckUrl { get; set; } = "https://www.gstatic.com/generate_204";

    public event Action<SessionStatusInfo>? StatusChanged;
    public event Action<string, string>? Notification; // title, message

    public SessionStatusInfo Status => _status;
    public bool HasSession => _journal != null || _status.State != SessionState.Inactive;
    public bool IsActive => _status.State is not (SessionState.Inactive or SessionState.Activating or SessionState.Deactivating) && _journal != null;
    public int? MixedPort => _ports?.Mixed;
    public int? ProbePort => _ports?.ProbeChain;
    public SessionJournal? Journal => _journal;
    public HelperClient? Helper => _helper;

    /// <summary>The running configuration, independent of edits saved for the next activation.</summary>
    public AppSettings? GetActiveSettingsSnapshot()
    {
        var active = _activeSettings;
        if (active == null) return null;
        var snapshot = SettingsStore.Clone(active);
        snapshot.SecondStage.ExpectedExitIp = _journal?.ExpectedExitIp ?? _verifiedIp ?? snapshot.SecondStage.ExpectedExitIp;
        return snapshot;
    }

    /// <summary>
    /// Tests the current user route without changing the gate or connecting to the original proxies.
    /// Network I/O runs outside the operation lock so monitoring and restoration remain available.
    /// </summary>
    public async Task<ChainTestResult> TestCurrentRouteAsync(IProgress<ChainStep>? progress = null,
        bool lookupIntel = true, CancellationToken ct = default)
    {
        ChainTestResult Failure(string detail, string? hint = null)
        {
            var step = new ChainStep("current", "当前受保护线路", StepStatus.Failed, detail, TimeSpan.Zero, hint);
            progress?.Report(step);
            return new ChainTestResult(new[] { step }, null, null, null, null, null, null) { Mode = RouteTestMode.Protected };
        }

        string sessionId;
        SidecarPorts ports;
        RoutingScope scope;
        string? expected;
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsActive || _ports == null || _activeSettings == null)
                return Failure("当前没有可检测的已激活线路，或环境正在切换。", "请等待激活或恢复完成后重新检测。");
            if (!_status.GateOpen || _status.State == SessionState.Blocked)
                return Failure($"当前线路保持阻断：{_status.Detail}", "请先在首页重新验证；线路检测不会打开闸门或绕过保护。");
            sessionId = _journal!.SessionId;
            ports = _ports;
            scope = _activeSettings.Protection.Scope;
            expected = _journal.ExpectedExitIp ?? _verifiedIp;
        }
        finally
        {
            _opLock.Release();
        }

        var result = await ProtectedRouteTester.RunAsync(ports.Mixed, ports.ProbeChain, scope, expected,
            progress, lookupIntel, ct).ConfigureAwait(false);

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Never present a completed probe as proof for a different or newly blocked session.
            if (!IsActive || _journal?.SessionId != sessionId || _ports != ports ||
                !_status.GateOpen || _status.State == SessionState.Blocked ||
                !string.Equals(expected, _journal.ExpectedExitIp ?? _verifiedIp, StringComparison.Ordinal))
            {
                var step = new ChainStep("session-changed", "当前会话状态", StepStatus.Failed,
                    "检测期间环境、期望出口或保护状态已变化，本次结果不能确认当前线路可用。", TimeSpan.Zero,
                    "请等待环境稳定后重新检测。");
                progress?.Report(step);
                return result with { Steps = result.Steps.Append(step).ToArray(), Intel = null };
            }
            return result;
        }
        finally
        {
            _opLock.Release();
        }
    }

    // =====================================================================================
    // Activation
    // =====================================================================================

    public async Task<ActivationResult> ActivateAsync(IProgress<ActivationStep>? progress = null, CancellationToken ct = default)
    {
        if (!await _opLock.WaitAsync(0, ct).ConfigureAwait(false))
            return new ActivationResult(false, "另一个操作正在进行中", Array.Empty<ActivationStep>(), null, Array.Empty<string>());
        var steps = new List<ActivationStep>();
        var notices = new List<string>();
        ChainTestResult? chain = null;
        void Step(string title, StepStatus status, string detail)
        {
            var s = new ActivationStep(title, status, detail);
            steps.Add(s);
            progress?.Report(s);
            Log.Info("Activate", $"[{status}] {title}: {detail}");
        }

        try
        {
            if (_journal != null) return new ActivationResult(false, "环境已处于激活状态", steps, null, notices);
            var settings = SettingsStore.Clone(_settings.Current);
            _activeSettings = settings;
            SetStatus(SessionState.Activating, "正在激活", "正在检查环境与线路…");

            // ---- 1. Validate ------------------------------------------------------------------
            var second = SettingsStore.ResolveSecondStage(settings);
            var err = second.Validate();
            if (err != null) return Fail($"二级代理配置无效：{err}");
            if (second.HasCredentials && string.IsNullOrEmpty(second.Password))
                return Fail("二级代理的密码尚未保存，请在“网络线路”页面重新输入。");
            var expected = settings.SecondStage.ExpectedExitIp?.Trim();
            if (string.IsNullOrEmpty(expected) && settings.Protection.RequireExpectedIp)
                return Fail("请先在“网络线路”中设置期望出口 IP（可在测试线路后一键填入）。");
            Step("检查配置", StepStatus.Passed, $"二级代理 {second}");

            // ---- 2. Inspect environment ---------------------------------------------------------
            ClashEnvironment? clash = null;
            if (settings.FirstStage.Mode == FirstStageMode.ClashLocalPort || settings.Protection.Level == ProtectionLevel.SystemTun)
            {
                clash = await ClashDetector.DetectAsync(settings.FirstStage.ControllerAddress, SecretStore.Get(SecretStore.ClashControllerSecret), ct).ConfigureAwait(false);
                if (clash.ControllerReachable && clash.ControllerSecret != null)
                    SecretStore.Set(SecretStore.ClashControllerSecret, clash.ControllerSecret);
            }

            if (settings.FirstStage.Mode == FirstStageMode.ClashLocalPort)
            {
                var port = settings.FirstStage.LocalPort;
                var socks = await ClashDetector.ProbeSocks5Async(port, ct).ConfigureAwait(false);
                var http = socks ? false : await ClashDetector.ProbeHttpAsync(port, ct).ConfigureAwait(false);
                if (!socks && !http)
                    return Fail($"一级代理端口 127.0.0.1:{port} 没有响应。请确认 {clash?.ClientName ?? "Clash"} 正在运行。");
                if (settings.FirstStage.LocalProtocol == ProxyProtocol.Socks5 && !socks)
                    settings.FirstStage.LocalProtocol = ProxyProtocol.Http;
                Step("检测一级线路", StepStatus.Passed, $"{clash?.ClientName ?? "本地代理"} 端口 {port}（{(socks ? "SOCKS5" : "HTTP")}）");
            }

            var mihomoPath = settings.Advanced.MihomoPathOverride is { Length: > 0 } p ? p : AppPaths.BundledMihomoPath;
            if (MihomoSidecar.VerifyBinary(mihomoPath) is { } binErr) return Fail(binErr);

            if (settings.Protection.Level == ProtectionLevel.SystemTun)
            {
                var foreignTun = NetworkInspector.Capture().TunAdapters.Where(t => !t.Name.Equals("CCNest", StringComparison.OrdinalIgnoreCase)).ToList();
                if (clash?.TunEnabled == true && !(settings.Integrations.DisableExistingClientTun && clash.ControllerReachable))
                    return Fail($"{clash.ClientName} 已开启 TUN 模式，且无法通过控制接口临时关闭。请先在 {clash.ClientName} 中关闭 TUN，或改用“标准模式”。");
                if (clash?.TunEnabled != true && foreignTun.Count > 0)
                    notices.Add($"检测到其他虚拟网卡：{string.Join("、", foreignTun.Select(t => t.Description))}。如果它属于其他 VPN/代理软件并处于活动状态，可能与 CCNest 的 TUN 冲突。");
            }

            if (!NetworkInspector.IsPortFree(settings.Advanced.MixedPort))
                return Fail($"CCNest 本地端口 {settings.Advanced.MixedPort} 已被其他程序占用，请在“设置 › 高级”中更换端口。");

            // ---- 3. Pre-test the chain (before changing anything) --------------------------------
            List<ProxyEndpoint>? firstHops = settings.FirstStage.Mode switch
            {
                FirstStageMode.ClashLocalPort => new() { new ProxyEndpoint { Protocol = settings.FirstStage.LocalProtocol, Host = settings.FirstStage.LocalHost, Port = settings.FirstStage.LocalPort } },
                FirstStageMode.ManualProxy => new() { SettingsStore.ResolveManualFirstStage(settings) },
                FirstStageMode.None => new(),
                _ => null, // imported node: tested through the core's first-stage probe below
            };

            if (firstHops != null)
            {
                chain = await ChainTester.RunAsync(firstHops, second, expected, null, lookupIntel: false, ct).ConfigureAwait(false);
                if (!chain.Passed)
                {
                    var f = chain.FirstFailure!;
                    return Fail($"线路预检未通过：{f.Title} — {f.Detail}" + (f.Hint != null ? $"\n建议：{f.Hint}" : ""));
                }
                Step("预检双重线路", StepStatus.Passed, $"一级出口 {chain.FirstStageExitIp ?? "-"} → 二级出口 {chain.ExitIp}");
            }

            // ---- 4. Begin journal --------------------------------------------------------------
            _journal = new SessionJournal
            {
                Level = settings.Protection.Level,
                Scope = settings.Protection.Scope,
                CrashPolicy = settings.Protection.CrashPolicy,
                MixedPort = settings.Advanced.MixedPort,
                ExpectedExitIp = expected,
            };
            await ApplyChangeAsync(new RecoveryStartupChange(), _ => { StartupRegistration.AddRecovery(); return Task.CompletedTask; }).ConfigureAwait(false);

            // ---- 5. Start core ------------------------------------------------------------------
            var needFirstProbe = settings.FirstStage.Mode != FirstStageMode.None;
            _ports = SidecarPorts.Allocate(settings.Advanced.MixedPort, needFirstProbe);
            var spec = BuildSpec(settings, second, _ports, clash);

            if (settings.Protection.Level == ProtectionLevel.SystemTun)
            {
                _helper ??= new HelperClient();
                await _helper.EnsureStartedAsync(settings.Protection.CrashPolicy, ct).ConfigureAwait(false);
                Step("管理员授权", StepStatus.Passed, "提权助手已启动");

                if (clash?.TunEnabled == true && clash.ControllerAddress != null)
                {
                    var change = new ClashTunChange { ControllerAddress = clash.ControllerAddress, OriginalEnabled = true, AppliedEnabled = false };
                    await ApplyChangeAsync(change, async _ =>
                    {
                        using var ctl = new MihomoController(clash.ControllerAddress, clash.ControllerSecret);
                        await ctl.PatchConfigsAsync(new JsonObject { ["tun"] = new JsonObject { ["enable"] = false } }, ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    Step("避免 TUN 冲突", StepStatus.Passed, $"已临时关闭 {clash.ClientName} 的 TUN（结束时恢复）");
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }

                var config = MihomoConfigBuilder.Build(spec with { Tun = true });
                await ApplyChangeAsync(new TunCoreChange(), async _ =>
                {
                    await _helper.StartTunCoreAsync(new StartTunCoreRequest { Config = config, SourceBinary = mihomoPath }, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
                _controller = new MihomoController($"127.0.0.1:{_ports.Controller}", _ports.Secret);
                SecretRedactor.Register(_ports.Secret);
                Step("启动 TUN 内核", StepStatus.Passed, "CCNest 虚拟网卡已创建，系统流量将进入专属线路");
            }
            else
            {
                var config = MihomoConfigBuilder.Build(spec);
                _sidecar = new MihomoSidecar(mihomoPath, AppPaths.MihomoHomeDir);
                var (ok, output) = await _sidecar.TestConfigAsync(config, ct).ConfigureAwait(false);
                if (!ok) throw new InvalidOperationException("内核配置校验失败：" + output);
                await ApplyChangeAsync(new SidecarChange { MixedPort = _ports.Mixed }, async c =>
                {
                    await _sidecar.StartAsync(config, _ports, ct).ConfigureAwait(false);
                    ((SidecarChange)c).Pid = _sidecar.ProcessId ?? 0;
                }).ConfigureAwait(false);
                _sidecar.UnexpectedExit += OnSidecarExit;
                _controller = _sidecar.Controller;
                Step("启动代理内核", StepStatus.Passed, $"本地端口 127.0.0.1:{_ports.Mixed}（入口闸门暂时关闭）");
            }

            // ---- 6. Verify through the real core, then open the gate --------------------------------
            if (firstHops == null && _ports.ProbeFirst is { } probeFirst)
            {
                chain = await ChainTester.RunAsync(new[] { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = probeFirst } },
                    second, expected, null, lookupIntel: false, ct).ConfigureAwait(false);
                if (!chain.Passed)
                {
                    var f = chain.FirstFailure!;
                    throw new InvalidOperationException($"线路检测未通过：{f.Title} — {f.Detail}");
                }
            }

            var verified = await VerifyExitAsync(_ports.ProbeChain, expected ?? chain?.ExitIp, ct).ConfigureAwait(false);
            if (verified.Error != null) throw new InvalidOperationException(verified.Error);
            _verifiedIp = verified.Ip;
            _journal.VerifiedExitIp = verified.Ip;
            await _controller!.SelectProxyAsync(MihomoConfigBuilder.GateName, MihomoConfigBuilder.StaticName, ct).ConfigureAwait(false);
            var viaGate = await VerifyExitAsync(_ports.Mixed, verified.Ip, ct).ConfigureAwait(false);
            if (viaGate.Error != null) throw new InvalidOperationException("闸门开启后验证失败：" + viaGate.Error);
            Step("验证出口", StepStatus.Passed, $"出口 IP {verified.Ip} 已确认{(expected != null ? "，与期望一致" : "")}");

            // ---- 7. Optional first-stage node lock ------------------------------------------------
            if (settings.FirstStage.Mode == FirstStageMode.ClashLocalPort && settings.FirstStage.LockGroup is { Length: > 0 } group
                && settings.FirstStage.LockNode is { Length: > 0 } node && clash?.ControllerReachable == true)
            {
                var current = clash.Groups.FirstOrDefault(g => g.Name == group)?.Now;
                if (current != node)
                {
                    var change = new ClashSelectionChange { ControllerAddress = clash.ControllerAddress!, Group = group, OriginalNode = current, AppliedNode = node };
                    await ApplyChangeAsync(change, async _ =>
                    {
                        using var ctl = new MihomoController(clash.ControllerAddress!, clash.ControllerSecret);
                        await ctl.SelectProxyAsync(group, node, ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    Step("锁定一级节点", StepStatus.Passed, $"{group} → {node}");
                }
            }

            // ---- 8. Integrations --------------------------------------------------------------
            await ApplyIntegrationsAsync(settings, Step, notices, ct).ConfigureAwait(false);

            // ---- 9. Enforcement (WFP) ----------------------------------------------------------
            await ApplyEnforcementAsync(settings, clash, Step, notices, ct).ConfigureAwait(false);

            // ---- 10. Running apps notices -------------------------------------------------------
            var runningCode = ClaudeCodeManager.RunningClaudeCodePids().Count;
            if (runningCode > 0 && settings.Integrations.ConfigureClaudeCode)
                notices.Add($"检测到 {runningCode} 个在激活前启动的 Claude Code 进程，它们不会读取新的代理设置，请重新启动这些会话。");
            if (ClaudeDesktopManager.RunningPids().Count > 0)
                notices.Add("Claude Desktop 正在运行：它会跟随新的系统代理，但已建立的连接仍走原路径。建议完全退出后通过 CCNest 重新启动。");
            if (settings.Protection.Level == ProtectionLevel.Standard)
                notices.Add("标准模式只对遵循代理设置的应用生效；不遵循代理的流量不会经过二级代理（也不受 CCNest 阻断）。需要强制保护请使用“应用强制”或“全局 TUN”模式。");

            // ---- 11. Watchdog + active ----------------------------------------------------------
            _journal.Status = SessionStatus.Active;
            JournalStore.SaveSession(_journal);
            StartWatchdog();
            _failures = 0;
            _lastExitCheck = DateTime.Now;
            _activatedAt = DateTime.Now;
            UpdateProtectedStatus(verified.Ip, null, chain);
            StartMonitor();
            Step("完成", StepStatus.Passed, "Claude 专属环境已激活");
            return new ActivationResult(true, "Claude 专属环境已激活", steps, chain, notices);

            ActivationResult Fail(string message)
            {
                Step("激活失败", StepStatus.Failed, message);
                SetStatus(SessionState.Inactive, "未激活", message);
                _activeSettings = null;
                return new ActivationResult(false, message, steps, chain, notices);
            }
        }
        catch (Exception ex)
        {
            var message = ex is ElevationDeniedException ? "需要管理员授权才能使用该保护模式，已取消激活。" : ex.Message;
            Log.Error("Activate", "激活失败，正在回滚", ex);
            steps.Add(new ActivationStep("激活失败", StepStatus.Failed, message));
            progress?.Report(steps[^1]);
            await RollbackAsync().ConfigureAwait(false);
            SetStatus(SessionState.Inactive, "未激活", "激活失败，所有更改已回滚：" + message);
            return new ActivationResult(false, message, steps, chain, notices);
        }
        finally
        {
            _opLock.Release();
        }
    }

    private CoreConfigSpec BuildSpec(AppSettings settings, ProxyEndpoint second, SidecarPorts ports, ClashEnvironment? clash)
    {
        ProxyEndpoint? firstEp = settings.FirstStage.Mode switch
        {
            FirstStageMode.ClashLocalPort => new ProxyEndpoint { Protocol = settings.FirstStage.LocalProtocol, Host = settings.FirstStage.LocalHost, Port = settings.FirstStage.LocalPort },
            FirstStageMode.ManualProxy => SettingsStore.ResolveManualFirstStage(settings),
            _ => null,
        };
        JsonObject? node = null;
        if (settings.FirstStage.Mode == FirstStageMode.ImportedNode)
        {
            var yaml = SecretStore.Get(SecretStore.FirstStageNodeYaml) ?? throw new InvalidOperationException("尚未导入一级节点");
            node = MihomoConfigBuilder.NodeFromYaml(yaml);
        }

        var directNames = new List<string>();
        if (settings.Protection.Level == ProtectionLevel.SystemTun && settings.FirstStage.Mode == FirstStageMode.ClashLocalPort && clash?.CoreProcessName != null)
            directNames.Add(clash.CoreProcessName);

        return new CoreConfigSpec
        {
            MixedPort = ports.Mixed,
            ProbeChainPort = ports.ProbeChain,
            ProbeFirstPort = ports.ProbeFirst,
            ControllerPort = ports.Controller,
            Secret = ports.Secret,
            FirstStageMode = settings.FirstStage.Mode,
            FirstStageEndpoint = firstEp,
            FirstStageNode = node,
            SecondStage = second,
            Scope = settings.Protection.Scope,
            AllowLan = settings.Protection.AllowLan,
            StartGateClosed = true,
            DirectProcessNames = directNames,
            DirectProcessPaths = clash?.CorePath != null && directNames.Count > 0 ? new[] { clash.CorePath } : Array.Empty<string>(),
        };
    }

    private async Task ApplyIntegrationsAsync(AppSettings settings, Action<string, StepStatus, string> step, List<string> notices, CancellationToken ct)
    {
        var port = _ports!.Mixed;
        if (settings.Integrations.SetSystemProxy)
        {
            var original = SystemProxy.Backend.Read();
            var applied = SystemProxy.For(port);
            await ApplyChangeAsync(new SystemProxyChange { Original = original, Applied = applied }, _ =>
            {
                SystemProxy.Backend.Write(applied);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            step("系统代理", StepStatus.Passed, $"Windows 系统代理 → 127.0.0.1:{port}（原设置：{original.Describe()}）");
        }

        if (settings.Integrations.ConfigureClaudeCode)
        {
            var path = ClaudeCodeManager.SettingsPath;
            try
            {
                var exists = File.Exists(path);
                var original = ClaudeCodeManager.ReadManagedEnv(path);
                var envExisted = exists && ClaudeCodeManager.ReadSettings(path)["env"] is JsonObject;
                var applied = ClaudeCodeManager.ProxyEnvFor(port, original["NO_PROXY"]);
                var change = new ClaudeCodeEnvChange { SettingsPath = path, FileExisted = exists, EnvExisted = envExisted, Original = original, Applied = applied };
                await ApplyChangeAsync(change, _ =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    ClaudeCodeManager.WriteManagedEnv(path, applied, removeEmptyEnv: false);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                step("Claude Code", StepStatus.Passed, $"已在 {path} 中设置 HTTPS_PROXY（新启动的 Claude Code 生效）");
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                step("Claude Code", StepStatus.Warning, ex.Message);
                notices.Add("Claude Code 配置未自动修改：" + ex.Message + "。可使用“打开 Claude Code 终端”按钮，或手动设置 HTTPS_PROXY。");
            }
        }

        if (settings.Integrations.SetUserEnvironmentProxy)
        {
            var url = $"http://127.0.0.1:{port}";
            foreach (var (name, value) in new[] { ("HTTPS_PROXY", url), ("HTTP_PROXY", url), ("NO_PROXY", ClaudeCodeManager.MergeNoProxy(EnvironmentVars.GetUser("NO_PROXY"))) })
            {
                var original = EnvironmentVars.GetUser(name);
                await ApplyChangeAsync(new UserEnvChange { Name = name, Original = original, Applied = value }, _ =>
                {
                    EnvironmentVars.SetUser(name, value);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            step("环境变量", StepStatus.Passed, "已设置用户级 HTTPS_PROXY / HTTP_PROXY（新启动的程序生效）");
        }

        if (settings.SystemEnv.ApplyWithSession)
        {
            var applied = await SystemEnvService.ApplyAsync(settings.SystemEnv, c => ApplyChangeAsync(c.Change, _ => c.Apply()), ct).ConfigureAwait(false);
            foreach (var (title, timing) in applied)
                step(title, StepStatus.Passed, SystemEnvService.Describe(timing));
            if (applied.Any(a => a.Timing == EffectTiming.SignOut))
                notices.Add("部分系统设置（显示语言）需要注销后才会生效。");
        }
    }

    private async Task ApplyEnforcementAsync(AppSettings settings, ClashEnvironment? clash, Action<string, StepStatus, string> step, List<string> notices, CancellationToken ct)
    {
        var level = settings.Protection.Level;
        if (level == ProtectionLevel.Standard) return;

        var firewall = FirewallStatus.Read();
        if (!firewall.WfpAvailable)
            throw new InvalidOperationException("Windows 基本筛选引擎（BFE）服务未运行，无法建立强制保护。请在“服务”中启动 Base Filtering Engine。");
        if (firewall.DefenderDisabledSomewhere)
            notices.Add("检测到 Windows Defender 防火墙已关闭。CCNest 使用独立的 WFP 规则，强制保护不依赖防火墙开关。");

        _helper ??= new HelperClient();
        await _helper.EnsureStartedAsync(settings.Protection.CrashPolicy, ct).ConfigureAwait(false);

        var blockedPorts = clash?.Ports.Select(p => p.Port).ToList() ?? new List<int>();
        if (settings.FirstStage.Mode == FirstStageMode.ClashLocalPort && !blockedPorts.Contains(settings.FirstStage.LocalPort))
            blockedPorts.Add(settings.FirstStage.LocalPort);

        var dynamic = settings.Protection.CrashPolicy == CrashPolicy.AutoRestore;
        KillSwitchRequest request;
        if (level == ProtectionLevel.AppEnforced)
        {
            var apps = new List<string>();
            var desktop = await ClaudeDesktopManager.DetectAsync(ct).ConfigureAwait(false);
            if (desktop.ExecutablePath != null) apps.Add(desktop.ExecutablePath);
            var code = ClaudeCodeManager.FindExecutable(out _);
            if (code != null && code.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) apps.Add(code);
            var desktopCodeDir = Path.Combine(AppPaths.RoamingAppData, "Claude", "claude-code");
            if (Directory.Exists(desktopCodeDir))
                apps.AddRange(Directory.EnumerateFiles(desktopCodeDir, "claude.exe", SearchOption.AllDirectories));
            if (apps.Count == 0)
            {
                notices.Add("未找到 Claude Desktop 或 Claude Code 可执行文件，应用强制规则未启用。");
                return;
            }
            request = new KillSwitchRequest
            {
                Mode = KillSwitchMode.AppScoped,
                Dynamic = dynamic,
                ScopedAppPaths = apps,
                BlockedLoopbackPorts = blockedPorts,
                AllowLan = settings.Protection.AllowLan,
            };
        }
        else
        {
            request = new KillSwitchRequest
            {
                Mode = KillSwitchMode.SystemWide,
                Dynamic = dynamic,
                AllowElevatedCore = true,
                AllowedProcessIds = settings.FirstStage.Mode == FirstStageMode.ClashLocalPort && clash?.CorePid is { } pid ? new() { pid } : new(),
                AllowedAppPaths = settings.FirstStage.Mode == FirstStageMode.ClashLocalPort && clash?.CorePath is { } path ? new() { path } : new(),
                TunAlias = "CCNest",
                BlockedLoopbackPorts = settings.FirstStage.Mode == FirstStageMode.ClashLocalPort ? blockedPorts : new(),
                AllowLan = settings.Protection.AllowLan,
            };
        }

        var change = new KillSwitchChange { Mode = request.Mode, Dynamic = dynamic };
        await ApplyChangeAsync(change, async _ => await _helper.ApplyKillSwitchAsync(request, ct).ConfigureAwait(false)).ConfigureAwait(false);
        step(change.Title, StepStatus.Passed, request.Mode == KillSwitchMode.AppScoped
            ? $"已限制 {request.ScopedAppPaths.Count} 个 Claude 程序只能通过 CCNest 联网"
            : "绕过 CCNest 虚拟网卡的流量将被系统阻断");

        // Prove the enforcement works: a protected path must still reach the exit afterwards.
        var check = await VerifyExitAsync(_ports!.Mixed, _verifiedIp, ct).ConfigureAwait(false);
        if (check.Error != null) throw new InvalidOperationException("启用强制保护后线路验证失败：" + check.Error);
    }

    /// <summary>Journals a change (write-ahead), applies it, and marks it applied.</summary>
    private async Task ApplyChangeAsync(ChangeRecord change, Func<ChangeRecord, Task> apply)
    {
        _journal!.Changes.Add(change);
        JournalStore.SaveSession(_journal);
        await apply(change).ConfigureAwait(false);
        change.State = ChangeState.Applied;
        JournalStore.SaveSession(_journal);
    }

    private async Task<(string? Ip, string? Error)> VerifyExitAsync(int port, string? expected, CancellationToken ct)
    {
        string? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var obs = await IpEchoService.QuickAsync(port, _rotation++, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            if (obs.Success)
            {
                if (expected != null && !IpEchoService.SameIp(obs.Ip, expected))
                    return (obs.Ip, $"出口 IP {obs.Ip} 与期望 {expected} 不符");
                return (obs.Ip, null);
            }
            lastError = obs.Error;
            await Task.Delay(800, ct).ConfigureAwait(false);
        }
        return (null, $"无法通过专属线路获取出口 IP：{lastError}");
    }

    private async Task RollbackAsync()
    {
        StopMonitor();
        if (_journal != null)
        {
            var executor = new ChangeExecutor(_helper, StopSidecarAsync);
            await executor.RestoreAllAsync(_journal.Changes, () => JournalStore.SaveSession(_journal), force: false).ConfigureAwait(false);
            if (_helper != null) await _helper.ShutdownAsync(restore: true).ConfigureAwait(false);
            if (!_journal.HasPendingRestore) JournalStore.CloseSession(_journal);
            else JournalStore.SaveSession(_journal);
        }
        await StopSidecarAsync().ConfigureAwait(false);
        _controller = null;
        _journal = null;
        _activeSettings = null;
        StopWatchdog();
    }

    // =====================================================================================
    // Deactivation
    // =====================================================================================

    public async Task<DeactivationResult> DeactivateAsync(bool force = false, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_journal == null)
            {
                SetStatus(SessionState.Inactive, "未激活", "Claude 专属环境未激活。");
                return new DeactivationResult(true, Array.Empty<RestoreOutcome>(), Array.Empty<string>());
            }
            SetStatus(SessionState.Deactivating, "正在恢复", "正在恢复原始网络与系统设置…");
            StopMonitor();
            _journal.Status = SessionStatus.Deactivating;
            JournalStore.SaveSession(_journal);

            if (_journal.Changes.Any(c => c.RequiresElevation && c.NeedsRestore) && _helper is not { IsConnected: true })
            {
                try
                {
                    _helper ??= new HelperClient();
                    await _helper.EnsureStartedAsync(_journal.CrashPolicy, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn("Deactivate", $"无法启动提权助手：{ex.Message}");
                }
            }

            var executor = new ChangeExecutor(_helper, StopSidecarAsync);
            var outcomes = await executor.RestoreAllAsync(_journal.Changes, () => JournalStore.SaveSession(_journal), force, ct).ConfigureAwait(false);
            if (_helper != null) await _helper.ShutdownAsync(restore: true, ct).ConfigureAwait(false);
            await StopSidecarAsync().ConfigureAwait(false);

            var notices = new List<string>();
            if (outcomes.Any(o => o.Change is ClaudeCodeEnvChange && o.Success))
                notices.Add("正在运行的 Claude Code 会话仍使用激活时的环境，重新启动后生效。");
            var conflicts = outcomes.Where(o => o.Change.State == ChangeState.Conflict).ToList();
            if (conflicts.Count > 0)
                notices.Add($"{conflicts.Count} 项设置在会话期间被其他程序修改，CCNest 未覆盖。可在“恢复中心”查看并选择强制恢复。");

            _journal.CleanShutdown = !_journal.HasPendingRestore;
            if (_journal.CleanShutdown || !_journal.Changes.Any(c => c.State == ChangeState.RestoreFailed))
                JournalStore.CloseSession(_journal);
            else
                JournalStore.SaveSession(_journal);
            var ok = outcomes.All(o => o.Success || o.Change.State == ChangeState.Conflict);
            _journal = null;
            _controller = null;
            _activeSettings = null;
            StopWatchdog();
            SetStatus(SessionState.Inactive, ok ? "已恢复" : "部分未恢复",
                ok ? "Claude 专属环境已结束，原始设置已恢复。" : "部分设置未能恢复，请查看恢复报告。");
            return new DeactivationResult(ok, outcomes, notices);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>Fast synchronous-ish restore used when Windows is shutting down or signing out.</summary>
    public void EmergencyRestoreForShutdown()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            DeactivateAsync(force: false, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Session", "关机时恢复失败，将在下次登录时处理", ex);
        }
    }

    private async Task StopSidecarAsync()
    {
        var s = _sidecar;
        _sidecar = null;
        if (s != null)
        {
            s.UnexpectedExit -= OnSidecarExit;
            await s.DisposeAsync().ConfigureAwait(false);
        }
    }

    // =====================================================================================
    // Monitoring
    // =====================================================================================

    private void StartMonitor()
    {
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _monitorTask = Task.Run(() => MonitorLoopAsync(token));
    }

    private void StopMonitor()
    {
        _monitorCts?.Cancel();
        _monitorCts = null;
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = _activeSettings ?? _settings.Current;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_reverifyRequested ? 1 : settings.Monitor.HealthIntervalSeconds), ct).ConfigureAwait(false);
                await _opLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_journal == null || _controller == null) continue;
                    await MonitorTickAsync(settings, ct).ConfigureAwait(false);
                }
                finally
                {
                    _opLock.Release();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn("Monitor", $"监控检查出错：{ex.Message}");
            }
        }
    }

    private async Task MonitorTickAsync(AppSettings settings, CancellationToken ct)
    {
        _warnings.Clear();

        // 1. Core alive?
        var coreAlive = _sidecar?.IsRunning ?? (_helper != null && await SafeTunRunningAsync(ct).ConfigureAwait(false));
        if (!coreAlive)
        {
            SetStatus(SessionState.Degraded, "内核已停止", "CCNest 代理内核未运行。所有指向 CCNest 的流量已中断（不会回退到直连）。正在尝试重启…");
            await TryRestartSidecarAsync(ct).ConfigureAwait(false);
            return;
        }

        // 2. Re-verification after wake/network change
        if (_reverifyRequested)
        {
            _reverifyRequested = false;
            await ReverifyCoreAsync("网络环境变化后复核", ct).ConfigureAwait(false);
            return;
        }

        // 3. Chain health through the full chain
        var (delay, error) = await _controller!.DelayAsync(MihomoConfigBuilder.StaticName, HealthCheckUrl, 10000, ct).ConfigureAwait(false);
        if (delay == null)
        {
            _failures++;
            if (_failures >= settings.Monitor.FailureThreshold)
            {
                SetStatus(SessionState.Degraded, "二级线路中断",
                    $"连续 {_failures} 次无法经二级代理访问网络（{error}）。Claude 流量已中断，CCNest 不会回退到一级线路或直连。",
                    gateOpen: _status.GateOpen);
                if (_failures == settings.Monitor.FailureThreshold)
                    Notification?.Invoke("二级线路中断", "Claude 专属线路暂时不可用，流量已被安全中断，未发生回退。");
            }
            return;
        }

        var wasDegraded = _status.State == SessionState.Degraded;
        _failures = 0;

        // 4. Exit IP
        if ((DateTime.Now - _lastExitCheck).TotalSeconds >= settings.Monitor.ExitIpIntervalSeconds || wasDegraded)
        {
            _lastExitCheck = DateTime.Now;
            var obs = await IpEchoService.QuickAsync(_ports!.ProbeChain, _rotation++, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            if (obs.Success)
            {
                var expected = _journal!.ExpectedExitIp ?? _verifiedIp;
                if (expected != null && !IpEchoService.SameIp(expected, obs.Ip))
                {
                    await HandleExitMismatchAsync(obs.Ip!, expected, settings, ct).ConfigureAwait(false);
                    return;
                }
                _verifiedIp = obs.Ip;
            }
        }

        // 5. External modifications
        if (settings.Integrations.SetSystemProxy && _journal!.Changes.OfType<SystemProxyChange>().FirstOrDefault() is { State: ChangeState.Applied } sp)
        {
            var now = SystemProxy.Backend.Read();
            if (!now.Equivalent(sp.Applied))
                _warnings.Add($"系统代理已被其他程序改为：{now.Describe()}。遵循系统代理的应用可能不再经过 CCNest。");
        }

        if (_status.State == SessionState.Blocked) return; // stays blocked until the user acts
        UpdateProtectedStatus(_verifiedIp, delay, null);
    }

    private async Task<bool> SafeTunRunningAsync(CancellationToken ct)
    {
        try { return await _helper!.TunCoreRunningAsync(ct).ConfigureAwait(false); }
        catch { return false; }
    }

    private async Task HandleExitMismatchAsync(string observed, string expected, AppSettings settings, CancellationToken ct)
    {
        Log.Warn("Monitor", $"出口 IP 变化：期望 {expected}，实际 {observed}");
        if (settings.Protection.BlockOnExitMismatch)
        {
            await CloseGateAsync(ct).ConfigureAwait(false);
            SetStatus(SessionState.Blocked, "出口 IP 已变化，已阻断",
                $"检测到出口 IP 从 {expected} 变为 {observed}。为防止使用错误的出口，CCNest 已阻断专属线路的全部流量。请确认后选择“重新验证”或“接受新 IP”。",
                gateOpen: false, exitIp: observed);
            Notification?.Invoke("出口 IP 已变化", $"实际出口 {observed} 与期望 {expected} 不符，已阻断流量。");
        }
        else
        {
            _warnings.Add($"出口 IP 从 {expected} 变为 {observed}（未启用自动阻断）");
            UpdateProtectedStatus(observed, null, null);
            Notification?.Invoke("出口 IP 已变化", $"实际出口 {observed} 与期望 {expected} 不符。");
        }
    }

    private async Task CloseGateAsync(CancellationToken ct)
    {
        try
        {
            await _controller!.SelectProxyAsync(MihomoConfigBuilder.GateName, "REJECT", ct).ConfigureAwait(false);
            await _controller.CloseAllConnectionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Monitor", "关闭闸门失败", ex);
        }
    }

    /// <summary>Re-verifies the exit (gate closed during the check) and reopens the gate on success.</summary>
    public async Task<bool> ReverifyAsync(string reason, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReverifyCoreAsync(reason, ct).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>Caller must hold <see cref="_opLock"/> (the monitor does).</summary>
    private async Task<bool> ReverifyCoreAsync(string reason, CancellationToken ct)
    {
        if (_controller == null || _ports == null || _journal == null) return false;
        var settings = _activeSettings ?? _settings.Current;
        if (settings.Protection.ReverifyOnWakeOrNetworkChange || _status.State == SessionState.Blocked)
            await CloseGateAsync(ct).ConfigureAwait(false);
        SetStatus(SessionState.Blocked, "正在复核", $"{reason}：正在确认出口 IP…", gateOpen: false);
        var expected = _journal.ExpectedExitIp ?? _verifiedIp;
        var result = await VerifyExitAsync(_ports.ProbeChain, expected, ct).ConfigureAwait(false);
        if (result.Error != null)
        {
            if (result.Ip != null)
                SetStatus(SessionState.Blocked, "出口 IP 不符，保持阻断", result.Error, gateOpen: false, exitIp: result.Ip);
            else
                SetStatus(SessionState.Degraded, "线路不可用", result.Error + "。流量保持中断。", gateOpen: false);
            return false;
        }
        await _controller.SelectProxyAsync(MihomoConfigBuilder.GateName, MihomoConfigBuilder.StaticName, ct).ConfigureAwait(false);
        _verifiedIp = result.Ip;
        _failures = 0;
        UpdateProtectedStatus(result.Ip, null, null);
        Log.Info("Session", $"{reason}：出口已确认 {result.Ip}，闸门重新开启");
        return true;
    }

    /// <summary>User accepted a new exit IP: update the expected IP and reopen.</summary>
    public async Task<bool> AcceptNewExitIpAsync(string ip, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _settings.Update(s => s.SecondStage.ExpectedExitIp = ip);
            if (_journal != null)
            {
                _journal.ExpectedExitIp = ip;
                JournalStore.SaveSession(_journal);
            }
            return await ReverifyCoreAsync("已接受新的出口 IP", ct).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    public void OnSuspend()
    {
        if (_journal == null) return;
        Log.Info("Session", "系统即将睡眠");
        var settings = _activeSettings ?? _settings.Current;
        if (settings.Protection.ReverifyOnWakeOrNetworkChange && _controller != null)
        {
            try { _controller.SelectProxyAsync(MihomoConfigBuilder.GateName, "REJECT").Wait(TimeSpan.FromSeconds(2)); }
            catch { /* best effort */ }
        }
    }

    public void OnResumeOrNetworkChange(string reason, bool isResume = false)
    {
        if (_journal == null) return;
        // CCNest's own TUN adapter and route changes during activation trigger address-change events.
        if (!isResume && (DateTime.Now - _activatedAt).TotalSeconds < 20) return;
        Log.Info("Session", $"{reason}，将复核出口");
        _reverifyRequested = true;
    }

    private void OnSidecarExit(int? code, string output)
    {
        SetStatus(SessionState.Degraded, "内核已退出", $"CCNest 代理内核意外退出（代码 {code}）。指向 CCNest 的流量已中断（未回退直连），正在尝试重启。");
        Notification?.Invoke("代理内核已退出", "流量已安全中断，CCNest 正在尝试自动重启内核。");
    }

    private async Task TryRestartSidecarAsync(CancellationToken ct)
    {
        if (_sidecar == null || _ports == null || _activeSettings == null) return;
        try
        {
            var settings = _activeSettings;
            var second = SettingsStore.ResolveSecondStage(settings);
            var clash = settings.FirstStage.Mode == FirstStageMode.ClashLocalPort ? await ClashDetector.DetectAsync(settings.FirstStage.ControllerAddress, null, ct).ConfigureAwait(false) : null;
            var config = MihomoConfigBuilder.Build(BuildSpec(settings, second, _ports, clash));
            await _sidecar.StopAsync().ConfigureAwait(false);
            await _sidecar.StartAsync(config, _ports, ct).ConfigureAwait(false);
            _controller = _sidecar.Controller;
            var change = _journal?.Changes.OfType<SidecarChange>().FirstOrDefault();
            if (change != null) change.Pid = _sidecar.ProcessId ?? 0;
            await ReverifyCoreAsync("内核重启后", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetStatus(SessionState.Degraded, "内核无法重启", $"代理内核重启失败：{ex.Message}。流量保持中断。");
        }
    }

    // =====================================================================================
    // Watchdog
    // =====================================================================================

    private void StartWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !exe.EndsWith("CCNest.exe", StringComparison.OrdinalIgnoreCase)) return;
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"--watchdog --parent {Environment.ProcessId} --session {_journal!.SessionId}",
            };
            _watchdog = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("Session", $"无法启动守护进程：{ex.Message}");
        }
    }

    private void StopWatchdog()
    {
        try
        {
            if (_watchdog is { HasExited: false }) _watchdog.Kill();
        }
        catch { /* ignore */ }
        _watchdog = null;
    }

    // =====================================================================================
    // Status
    // =====================================================================================

    private void UpdateProtectedStatus(string? exitIp, int? latency, ChainTestResult? chain)
    {
        var s = _activeSettings ?? _settings.Current;
        var levelText = s.Protection.Level switch
        {
            ProtectionLevel.Standard => "标准模式",
            ProtectionLevel.AppEnforced => "应用强制模式",
            _ => "全局 TUN 模式",
        };
        _status = new SessionStatusInfo
        {
            State = SessionState.Protected,
            Headline = "受保护",
            Detail = $"Claude 流量经 一级线路 → 二级静态出口 {exitIp}（{levelText}）",
            ExitIp = exitIp,
            ExpectedIp = _journal?.ExpectedExitIp,
            ExitCountry = chain?.ClaudeEdgeCountry ?? chain?.ExitCountry ?? _status.ExitCountry,
            FirstStageExitIp = chain?.FirstStageExitIp ?? _status.FirstStageExitIp,
            LatencyMs = latency ?? _status.LatencyMs,
            LastCheck = DateTime.Now,
            Since = _status.Since ?? DateTime.Now,
            GateOpen = true,
            Level = s.Protection.Level,
            Scope = s.Protection.Scope,
            MixedPort = _ports?.Mixed ?? 0,
            Warnings = _warnings.ToList(),
            Intel = _status.Intel,
        };
        StatusChanged?.Invoke(_status);
    }

    private void SetStatus(SessionState state, string headline, string detail, bool? gateOpen = null, string? exitIp = null)
    {
        var s = _activeSettings ?? _settings.Current;
        _status = _status with
        {
            State = state,
            Headline = headline,
            Detail = detail,
            GateOpen = gateOpen ?? (state == SessionState.Protected),
            ExitIp = exitIp ?? (state == SessionState.Inactive ? null : _status.ExitIp),
            LastCheck = DateTime.Now,
            Since = state == SessionState.Inactive ? null : _status.Since,
            Level = s.Protection.Level,
            Scope = s.Protection.Scope,
            MixedPort = _ports?.Mixed ?? 0,
            Warnings = _warnings.ToList(),
        };
        StatusChanged?.Invoke(_status);
    }

    public void SetIntel(IpIntel intel)
    {
        _status = _status with { Intel = intel, ExitCountry = intel.CountryCode ?? _status.ExitCountry };
        StatusChanged?.Invoke(_status);
    }

    /// <summary>Precise statement of what the current configuration does and does not guarantee.</summary>
    public static IReadOnlyList<ProtectionScopeItem> DescribeScope(AppSettings s)
    {
        var items = new List<ProtectionScopeItem>();
        var tun = s.Protection.Level == ProtectionLevel.SystemTun;
        var app = s.Protection.Level == ProtectionLevel.AppEnforced;

        items.Add(new ProtectionScopeItem("Claude 专属浏览器", "始终经 CCNest 显式代理",
            "专属浏览器只认 CCNest 端口：线路或内核故障时无法联网，不会直连；WebRTC 非代理 UDP 已禁用。", true));
        items.Add(new ProtectionScopeItem("Claude Code",
            s.Integrations.ConfigureClaudeCode ? "settings.json 中的 HTTPS_PROXY" : "仅通过 CCNest 打开的终端",
            tun ? "全部经 TUN 与内核阻断保护，包括它启动的子进程。"
                : app ? "Claude Code 程序本身只能经 CCNest 联网（WFP 强制）；它启动的子进程（MCP 服务器、git、npm 等）不受强制。"
                : "新启动的 Claude Code 经 CCNest；已在运行的会话需重启；子进程是否遵循代理取决于各自实现。",
            tun || app));
        items.Add(new ProtectionScopeItem("Claude Desktop",
            s.Integrations.SetSystemProxy ? "Windows 系统代理 / CCNest 启动参数" : "仅通过 CCNest 启动时",
            tun ? "全部经 TUN 与内核阻断保护。"
                : app ? "Claude Desktop 只能经 CCNest 联网（WFP 强制），绕过代理的连接会被系统阻断。"
                : "遵循系统代理；已建立的旧连接需重启 Claude Desktop 才会切换。",
            tun || app));
        items.Add(new ProtectionScopeItem("其他应用（含普通 Chrome）",
            tun ? "全部系统流量" : s.Integrations.SetSystemProxy ? "遵循系统代理的应用" : "不受影响",
            tun ? "所有流量经二级出口；绕过虚拟网卡的流量被 WFP 阻断（局域网可选放行）。"
                : "遵循系统代理的应用会经 CCNest；其余流量保持原路径，不受 CCNest 保护或阻断。",
            tun));
        items.Add(new ProtectionScopeItem("云端执行环境",
            "无法控制",
            "Claude 网页版的云端代码执行、Claude Code 云端会话等运行在 Anthropic 服务器上，其网络出口与本机无关。", false));
        return items;
    }

    public async ValueTask DisposeAsync()
    {
        StopMonitor();
        await StopSidecarAsync().ConfigureAwait(false);
        if (_helper != null) await _helper.DisposeAsync().ConfigureAwait(false);
    }
}
