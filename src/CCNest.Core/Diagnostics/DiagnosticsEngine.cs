using System.Net;
using CCNest.Core.Browser;
using CCNest.Core.Claude;
using CCNest.Core.Elevation;
using CCNest.Core.Infrastructure;
using CCNest.Core.Mihomo;
using CCNest.Core.Native;
using CCNest.Core.Proxy;
using CCNest.Core.Repair;
using CCNest.Core.Session;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;

namespace CCNest.Core.Diagnostics;

public sealed record DiagnosticsOptions
{
    public bool IncludeChainTest { get; init; } = true;
    public bool IncludeDnsLeakTest { get; init; } = true;
    public bool IncludeDirectChecks { get; init; } = true;
    public BrowserCheckResult? BrowserCheck { get; init; }
}

/// <summary>
/// Runs all environment checks. Network checks run concurrently; every check is isolated so a failure
/// in one never hides the others. Results are facts and actionable findings — CCNest deliberately does
/// not produce speculative "account risk" scores.
/// </summary>
public sealed class DiagnosticsEngine
{
    private sealed record RouteContext(bool Protected, string? SessionId);
    private readonly SettingsStore _settings;
    private readonly SessionManager? _session;

    public DiagnosticsEngine(SettingsStore settings, SessionManager? session)
    {
        _settings = settings;
        _session = session;
    }

    public async Task<DiagnosticReport> RunAsync(DiagnosticsOptions options, IProgress<DiagnosticItem>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<DiagnosticItem>();
        // Saved settings can differ from the running core. Keep the route selected for this run,
        // including an in-progress/blocked session, so a later check never falls back to raw hops.
        var protectedRoute = _session?.HasSession == true;
        var route = new RouteContext(protectedRoute, protectedRoute ? _session?.Journal?.SessionId : null);
        var s = (protectedRoute ? _session?.GetActiveSettingsSnapshot() : null) ?? SettingsStore.Clone(_settings.Current);
        void Add(DiagnosticItem item)
        {
            lock (items) items.Add(item);
            progress?.Report(item);
        }

        async Task Guard(string id, DiagCategory cat, string title, Func<Task> run)
        {
            try { await run().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Diagnostics", $"{title} 检查失败：{ex.Message}");
                Add(new DiagnosticItem { Id = id, Category = cat, Title = title, Status = DiagStatus.Unknown, Detected = "检查过程中出错：" + ex.Message });
            }
        }

        // Local checks first (fast), then network checks in parallel.
        await Guard("orphan", DiagCategory.Security, "会话状态", () => { CheckOrphan(Add); return Task.CompletedTask; }).ConfigureAwait(false);
        await Guard("core", DiagCategory.Security, "代理内核", () => { CheckCore(s, Add); return Task.CompletedTask; }).ConfigureAwait(false);
        await Guard("firewall", DiagCategory.Security, "防火墙与 WFP", () => { CheckFirewall(Add); return Task.CompletedTask; }).ConfigureAwait(false);
        await Guard("sysproxy", DiagCategory.Routing, "Windows 系统代理", () => CheckSystemProxyAsync(s, Add)).ConfigureAwait(false);
        await Guard("env", DiagCategory.Routing, "代理环境变量", () => { CheckEnvVars(Add); return Task.CompletedTask; }).ConfigureAwait(false);
        await Guard("routes", DiagCategory.Routing, "路由与虚拟网卡", () => { CheckRoutes(Add); return Task.CompletedTask; }).ConfigureAwait(false);
        await Guard("browser", DiagCategory.Browser, "专属浏览器", () => { CheckBrowser(s, Add); return Task.CompletedTask; }).ConfigureAwait(false);

        var clashTask = Guard("clash", DiagCategory.Proxy, "现有 Clash / Mihomo", () => CheckClashAsync(s, route, Add, ct));
        var claudeTask = Guard("claude", DiagCategory.Claude, "Claude 应用", () => CheckClaudeAppsAsync(Add, ct));
        var regionalTask = Guard("regional", DiagCategory.System, "Windows 区域与语言", () => CheckRegionalAsync(s, Add, ct));
        var dnsTask = Guard("dns-system", DiagCategory.Dns, "系统 DNS", () => CheckSystemDnsAsync(Add));
        var directTask = options.IncludeDirectChecks ? Guard("direct", DiagCategory.Network, "本机直连出口", () => CheckDirectAsync(Add, ct)) : Task.CompletedTask;
        await Task.WhenAll(clashTask, claudeTask, regionalTask, dnsTask, directTask).ConfigureAwait(false);

        ChainTestResult? chain = null;
        if (options.IncludeChainTest && (route.Protected || !string.IsNullOrWhiteSpace(s.SecondStage.Endpoint.Host)))
            await Guard("chain", DiagCategory.Proxy, "双重代理线路", async () => chain = await CheckChainAsync(s, route, Add, ct).ConfigureAwait(false)).ConfigureAwait(false);

        if (options.IncludeDnsLeakTest && (route.Protected || chain?.Passed == true))
            await Guard("dns-leak", DiagCategory.Dns, "DNS 泄漏检测", () => CheckDnsLeakAsync(s, route, chain, Add, ct)).ConfigureAwait(false);

        await Guard("clock", DiagCategory.System, "系统时间", () => CheckClockAsync(route, Add, ct)).ConfigureAwait(false);

        if (options.BrowserCheck != null)
            await Guard("browser-env", DiagCategory.Browser, "专属浏览器环境", () => { CheckBrowserEnvironment(s, options.BrowserCheck, chain, Add); return Task.CompletedTask; }).ConfigureAwait(false);

        var order = Enum.GetValues<DiagCategory>().ToList();
        var sorted = items.OrderBy(i => order.IndexOf(i.Category)).ThenBy(i => i.Status switch
        {
            DiagStatus.Error => 0, DiagStatus.Warning => 1, DiagStatus.Unknown => 2, DiagStatus.Info => 3, _ => 4,
        }).ToList();
        return new DiagnosticReport(started, sw.Elapsed, sorted);
    }

    // ---------------------------------------------------------------------------------------------

    private void CheckOrphan(Action<DiagnosticItem> add)
    {
        var orphan = JournalStore.FindOrphanedSession();
        if (orphan != null)
        {
            add(new DiagnosticItem
            {
                Id = "orphan", Category = DiagCategory.Security, Title = "上次会话未正常结束", Status = DiagStatus.Error,
                Detected = $"会话 {orphan.SessionId}（{orphan.StartedAt:MM-dd HH:mm} 开始）仍有 {orphan.Changes.Count(c => c.NeedsRestore)} 项更改未恢复",
                Explanation = "CCNest 上次可能崩溃或被强制结束。按照“安全优先”策略，相关设置保持原样，直到你决定恢复。",
                Repair = RepairService.RestoreOrphanedSession(orphan),
                Details = orphan.Changes.Where(c => c.NeedsRestore).Select(c => new KeyValuePair<string, string>(c.Title, c.Describe)).ToList(),
            });
            return;
        }

        var standalone = JournalStore.LoadSystemEnv().Changes.Where(c => c.NeedsRestore).ToList();
        var status = _session?.Status;
        add(new DiagnosticItem
        {
            Id = "session", Category = DiagCategory.Security, Title = "Claude 专属环境",
            Status = status?.State is SessionState.Degraded or SessionState.Blocked ? DiagStatus.Warning : DiagStatus.Info,
            Detected = status == null || status.State == SessionState.Inactive ? "未激活" : $"{status.Headline}：{status.Detail}",
            Explanation = standalone.Count > 0 ? $"另有 {standalone.Count} 项独立修改（系统环境/修复）可在“恢复中心”撤销。" : null,
        });
    }

    private static void CheckCore(AppSettings s, Action<DiagnosticItem> add)
    {
        var path = s.Advanced.MihomoPathOverride is { Length: > 0 } p ? p : AppPaths.BundledMihomoPath;
        if (!File.Exists(path))
        {
            add(new DiagnosticItem
            {
                Id = "core", Category = DiagCategory.Security, Title = "代理内核", Status = DiagStatus.Error,
                Detected = $"找不到内核文件：{path}", ManualSteps = "请重新安装 CCNest，或在 设置 › 高级 中指定 mihomo.exe 的路径。",
            });
            return;
        }
        var pinned = MihomoBinary.IsPinned(path);
        add(new DiagnosticItem
        {
            Id = "core", Category = DiagCategory.Security, Title = "代理内核", Status = pinned ? DiagStatus.Ok : DiagStatus.Warning,
            Detected = pinned ? $"mihomo {MihomoBinary.PinnedVersion}（完整性校验通过）" : "内核文件与 CCNest 内置版本不一致",
            Explanation = pinned ? "CCNest 使用独立的私有内核实例，不会修改你现有 Clash 客户端的配置文件。"
                : "标准模式仍可使用；全局 TUN 模式要求内核通过完整性校验才会以管理员权限运行。",
        });
    }

    private static void CheckFirewall(Action<DiagnosticItem> add)
    {
        var fw = FirewallStatus.Read();
        var profiles = $"域 {OnOff(fw.DomainEnabled)} / 专用 {OnOff(fw.PrivateEnabled)} / 公用 {OnOff(fw.PublicEnabled)}";
        add(new DiagnosticItem
        {
            Id = "firewall", Category = DiagCategory.Security, Title = "Windows 防火墙与筛选引擎",
            Status = fw.WfpAvailable ? (fw.DefenderDisabledSomewhere ? DiagStatus.Info : DiagStatus.Ok) : DiagStatus.Error,
            Detected = $"Defender 防火墙：{profiles}（服务 {Svc(fw.FirewallService)}）；基本筛选引擎 BFE：{Svc(fw.BaseFilteringEngine)}",
            Explanation = fw.WfpAvailable
                ? "CCNest 的强制保护直接使用 Windows 筛选平台（WFP）规则，即使 Defender 防火墙已关闭也能生效。"
                : "BFE 服务未运行：Windows 筛选平台不可用，“应用强制”和“全局 TUN”模式无法提供阻断保护。",
            ManualSteps = fw.WfpAvailable ? null : "在“服务”（services.msc）中将 Base Filtering Engine 设为自动并启动。",
            Uncertainty = fw.Error,
        });

        if (HelperClient.IsCurrentProcessElevated())
        {
            try
            {
                var count = Wfp.KillSwitch.CountActiveFilters();
                if (count > 0 && JournalStore.LoadSession() == null)
                    add(new DiagnosticItem
                    {
                        Id = "wfp-leftover", Category = DiagCategory.Security, Title = "残留的阻断规则", Status = DiagStatus.Error,
                        Detected = $"系统中存在 {count} 条 CCNest WFP 规则，但没有活动会话", Repair = RepairService.RemoveLeftoverWfpRules(count),
                    });
            }
            catch { /* best effort */ }
        }
    }

    private async Task CheckSystemProxyAsync(AppSettings s, Action<DiagnosticItem> add)
    {
        var state = SystemProxy.Backend.Read();
        var port = state.LoopbackPort();
        var active = _session?.IsActive == true;
        var ccnestPort = active ? _session!.MixedPort ?? s.Advanced.MixedPort : s.Advanced.MixedPort;
        var winhttp = await SystemProxy.ReadWinHttpProxyAsync().ConfigureAwait(false);
        var details = new List<KeyValuePair<string, string>> { new("WinHTTP 代理", winhttp ?? "直接访问") };
        if (state.Bypass != null) details.Add(new("例外列表", state.Bypass));

        if (port is { } p && RepairService.IsDeadLoopbackPort(p))
        {
            add(new DiagnosticItem
            {
                Id = "sysproxy", Category = DiagCategory.Routing, Title = "Windows 系统代理", Status = DiagStatus.Error,
                Detected = $"系统代理指向 127.0.0.1:{p}，但该端口没有程序在监听",
                Explanation = p == ccnestPort && !active
                    ? "这很可能是 CCNest 上次异常退出后保留的设置（安全优先策略）。遵循系统代理的程序目前无法联网。"
                    : "代理软件可能已退出但没有恢复系统代理，遵循系统代理的程序（浏览器等）将无法联网。",
                Repair = RepairService.ClearSystemProxy(state, "将系统代理恢复为直连，使浏览器等程序恢复联网。"),
                Details = details,
            });
            return;
        }

        var status = DiagStatus.Info;
        string? explanation = null;
        if (active && port != ccnestPort && s.Integrations.SetSystemProxy)
        {
            status = DiagStatus.Warning;
            explanation = "会话激活期间系统代理没有指向 CCNest，遵循系统代理的应用（如 Claude Desktop）可能绕过二级出口。";
        }
        else if (state.AutoConfigEnabled)
        {
            explanation = "使用了 PAC 自动配置脚本，实际走向取决于脚本内容。";
        }
        add(new DiagnosticItem
        {
            Id = "sysproxy", Category = DiagCategory.Routing, Title = "Windows 系统代理", Status = status,
            Detected = state.Describe() + (port == ccnestPort ? "（CCNest）" : ""), Explanation = explanation, Details = details,
            Uncertainty = "VPN/拨号连接拥有独立的代理设置，此处显示的是默认（局域网）连接。",
        });
    }

    private void CheckEnvVars(Action<DiagnosticItem> add)
    {
        var vars = EnvironmentVars.ProxyVariablesEverywhere();
        if (vars.Count == 0)
        {
            add(new DiagnosticItem { Id = "env", Category = DiagCategory.Routing, Title = "代理环境变量", Status = DiagStatus.Ok, Detected = "未设置 HTTP(S)_PROXY / ALL_PROXY" });
            return;
        }

        var dead = vars.Where(v => v.Scope == EnvScope.User && v.Name != "NO_PROXY" && LoopbackPortOf(v.Value) is { } p && RepairService.IsDeadLoopbackPort(p)).ToList();
        var machine = vars.Where(v => v.Scope == EnvScope.Machine && v.Name != "NO_PROXY").ToList();
        var details = vars.Select(v => new KeyValuePair<string, string>($"{v.Name}（{ScopeName(v.Scope)}）", v.Value)).ToList();
        if (dead.Count > 0)
        {
            add(new DiagnosticItem
            {
                Id = "env", Category = DiagCategory.Routing, Title = "代理环境变量", Status = DiagStatus.Error,
                Detected = $"{string.Join("、", dead.Select(d => d.Name))} 指向未在监听的本地端口",
                Explanation = "命令行工具（git、npm、Claude Code 等）会读取这些变量，端口失效会导致它们无法联网。",
                Repair = RepairService.RemoveUserEnvVars(dead, "删除指向失效端口的用户级代理变量。"),
                ManualSteps = machine.Count > 0 ? "系统级（Machine）变量需要管理员在“系统属性 › 环境变量”中手动修改。" : null,
                Details = details,
            });
            return;
        }
        add(new DiagnosticItem
        {
            Id = "env", Category = DiagCategory.Routing, Title = "代理环境变量", Status = DiagStatus.Info,
            Detected = string.Join("；", vars.Select(v => $"{v.Name}（{ScopeName(v.Scope)}）")),
            Explanation = "这些变量会影响命令行程序的联网路径。若它们指向一级代理端口，相关程序会绕过二级出口。",
            Details = details,
        });
    }

    private static void CheckRoutes(Action<DiagnosticItem> add)
    {
        var net = NetworkInspector.Capture();
        var tuns = net.TunAdapters.ToList();
        var details = new List<KeyValuePair<string, string>>
        {
            new("IPv4 默认路由", net.DefaultRouteV4 is { } r4 ? $"{r4.Name}（{r4.Description}）" : "无"),
            new("IPv6 默认路由", net.DefaultRouteV6 is { } r6 ? $"{r6.Name}（{r6.Description}）" : "无"),
        };
        details.AddRange(tuns.Select(t => new KeyValuePair<string, string>("虚拟网卡", $"{t.Name}（{t.Description}）")));

        var status = tuns.Count > 1 ? DiagStatus.Warning : DiagStatus.Info;
        add(new DiagnosticItem
        {
            Id = "routes", Category = DiagCategory.Routing, Title = "路由与虚拟网卡", Status = status,
            Detected = tuns.Count == 0 ? "未检测到活动的 TUN 虚拟网卡，系统流量走物理网卡"
                : $"检测到 {tuns.Count} 个活动虚拟网卡；IPv4 默认出口：{net.DefaultRouteV4?.Name ?? "无"}",
            Explanation = tuns.Count > 1 ? "多个 TUN 同时接管路由会互相冲突，导致流量走向不可预测。请只保留一个。" : null,
            Details = details,
        });

        if (net.HasGlobalIPv6)
        {
            var v6Tun = net.DefaultRouteV6 != null && net.DefaultRouteV6.LooksLikeTun;
            add(new DiagnosticItem
            {
                Id = "ipv6", Category = DiagCategory.Network, Title = "IPv6", Status = v6Tun ? DiagStatus.Info : DiagStatus.Warning,
                Detected = v6Tun ? "本机有公网 IPv6，IPv6 默认路由由虚拟网卡接管" : "本机有公网 IPv6 地址，且 IPv6 流量直接经物理网卡出去",
                Explanation = v6Tun ? null : "不遵循代理的程序可能通过 IPv6 直连暴露本机真实 IPv6。CCNest 的“全局 TUN”模式会接管并阻断这类流量；标准模式下专属浏览器与 Claude Code 走显式代理，不受影响。",
                Details = net.Interfaces.Where(i => i.IPv6Global.Count > 0).Select(i => new KeyValuePair<string, string>(i.Name, string.Join(", ", i.IPv6Global))).ToList(),
            });
        }
    }

    private static void CheckBrowser(AppSettings s, Action<DiagnosticItem> add)
    {
        var info = DedicatedBrowser.Inspect(s.Browser);
        if (info.Browser == null)
        {
            add(new DiagnosticItem
            {
                Id = "browser", Category = DiagCategory.Browser, Title = "专属浏览器", Status = DiagStatus.Error,
                Detected = "未找到 Chrome / Edge / Brave", ManualSteps = "请安装 Google Chrome，或在 设置 中指定 Chromium 内核浏览器路径。",
            });
            return;
        }
        add(new DiagnosticItem
        {
            Id = "browser", Category = DiagCategory.Browser, Title = "专属浏览器", Status = DiagStatus.Ok,
            Detected = $"{info.Browser.Name} {info.Browser.Version}；专属资料{(info.Exists ? $"已建立（{info.SizeBytes / 1024 / 1024} MB）" : "将在首次打开时创建")}{(info.Running ? "；正在运行" : "")}",
            Explanation = "专属资料与日常浏览器完全隔离（Cookie、扩展、历史记录互不影响），并跟随浏览器正常自动更新。",
            Details = new[] { new KeyValuePair<string, string>("资料位置", info.UserDataDir) },
        });
    }

    private async Task CheckClashAsync(AppSettings s, RouteContext route, Action<DiagnosticItem> add, CancellationToken ct)
    {
        var env = await ClashDetector.DetectAsync(s.FirstStage.ControllerAddress, SecretStore.Get(SecretStore.ClashControllerSecret), ct).ConfigureAwait(false);
        var systemProtected = route.Protected && s.Protection.Level == ProtectionLevel.SystemTun;
        var configuredListening = systemProtected && s.FirstStage.Mode == FirstStageMode.ClashLocalPort
            && IpHelper.GetTcpListeners().Any(l => l.Port == s.FirstStage.LocalPort && (env.CorePid == null || l.Pid == env.CorePid));
        var protocolProbeRestricted = configuredListening && env.Ports.All(p => p.Port != s.FirstStage.LocalPort);
        var details = new List<KeyValuePair<string, string>>();
        foreach (var p in env.Ports) details.Add(new("本地端口", $"{p.Port}（{(p.Socks5 && p.Http ? "混合" : p.Socks5 ? "SOCKS5" : "HTTP")}）"));
        if (protocolProbeRestricted)
            details.Add(new("本地端口", $"{s.FirstStage.LocalPort} 正在监听；全局保护会限制 CCNest 界面直接探测一级端口，协议未单独确认"));
        details.Add(new("控制接口", env.ControllerReachable ? $"{env.ControllerAddress}（mihomo {env.CoreVersion}）" : env.ControllerError ?? "未找到"));
        if (env.Mode != null) details.Add(new("代理模式", env.Mode));
        if (env.TunEnabled != null) details.Add(new("TUN", env.TunEnabled == true ? "开启" : "关闭"));
        if (env.ClientSystemProxyEnabled != null) details.Add(new("客户端系统代理", env.ClientSystemProxyEnabled == true ? "开启" : "关闭"));

        DiagStatus status;
        string detected;
        if (!env.Found)
        {
            status = s.FirstStage.Mode == FirstStageMode.ClashLocalPort ? DiagStatus.Error : DiagStatus.Info;
            detected = "未检测到正在运行的 Clash / Mihomo 客户端";
        }
        else
        {
            status = env.ClientProxyGuardEnabled == true && s.Integrations.SetSystemProxy ? DiagStatus.Warning : DiagStatus.Ok;
            detected = $"{env.ClientName}{(env.CoreProcessName != null ? $"（内核 {env.CoreProcessName}）" : "")}，"
                + (protocolProbeRestricted ? $"一级端口 {s.FirstStage.LocalPort} 正在监听（直接协议探测受保护规则限制）"
                    : $"推荐串联端口 {env.RecommendedPort?.Port.ToString() ?? "无"}");
        }

        // Health of the nodes currently selected in the client (through its controller, read-only).
        var nodeProblems = new List<string>();
        if (systemProtected)
            details.Add(new("一级节点测速", "全局保护期间不单独测试客户端节点；线路可用性以当前 CCNest 内核检测为准"));
        else if (env.ControllerReachable && env.ControllerAddress != null)
        {
            try
            {
                using var ctl = new MihomoController(env.ControllerAddress, env.ControllerSecret, TimeSpan.FromSeconds(12));
                foreach (var group in env.Groups.Where(g => g.Type == "Selector" && g.Name != "GLOBAL").Take(4))
                {
                    var (delay, error) = await ctl.DelayAsync(group.Name, "https://www.gstatic.com/generate_204", 6000, ct).ConfigureAwait(false);
                    details.Add(new($"节点测速：{group.Name}", delay is { } d ? $"{group.Now} · {d} ms" : $"{group.Now} · 失败（{error}）"));
                    if (delay == null) nodeProblems.Add($"{group.Name} → {group.Now}");
                }
            }
            catch (Exception ex)
            {
                details.Add(new("节点测速", "无法执行：" + ex.Message));
            }
        }
        if (nodeProblems.Count > 0 && status == DiagStatus.Ok)
            status = s.FirstStage.Mode == FirstStageMode.ClashLocalPort ? DiagStatus.Error : DiagStatus.Warning;

        var configured = s.FirstStage.Mode == FirstStageMode.ClashLocalPort && env.Ports.All(p => p.Port != s.FirstStage.LocalPort)
            && !configuredListening && env.Found;
        add(new DiagnosticItem
        {
            Id = "clash", Category = DiagCategory.Proxy, Title = "现有 Clash / Mihomo", Status = configured ? DiagStatus.Warning : status,
            Detected = detected + (configured ? $"；但设置中的一级端口 {s.FirstStage.LocalPort} 不在其监听列表中" : "")
                       + (nodeProblems.Count > 0 ? $"；当前节点测速失败：{string.Join("、", nodeProblems)}" : ""),
            Explanation = string.Join(" ", env.Notes),
            ManualSteps = nodeProblems.Count > 0
                ? "一级线路的当前节点无法访问互联网，请在 Clash 中切换可用节点后重新检测。"
                : env.ClientProxyGuardEnabled == true ? "请在 Clash Verge 设置中关闭“系统代理守卫”，否则它会把系统代理改回 Clash 端口。" : null,
            Details = details,
        });
    }

    private static async Task CheckClaudeAppsAsync(Action<DiagnosticItem> add, CancellationToken ct)
    {
        var code = await ClaudeCodeManager.DetectAsync(true, ct).ConfigureAwait(false);
        var details = new List<KeyValuePair<string, string>> { new("配置文件", code.SettingsPath) };
        foreach (var (k, v) in code.SettingsProxyEnv) details.Add(new($"settings.json env.{k}", v));
        foreach (var (k, v) in code.ManagedProxyEnv) details.Add(new($"托管策略 env.{k}", v));
        if (code.RunningPids.Count > 0) details.Add(new("正在运行", $"{code.RunningPids.Count} 个进程"));

        RepairAction? repair = null;
        var status = code.Installed ? DiagStatus.Ok : DiagStatus.Info;
        string? explanation = code.Installed ? "Claude Code 只支持 HTTP 代理（不支持 SOCKS），CCNest 会为其提供 HTTP 端口。" : null;
        var deadKeys = code.SettingsProxyEnv
            .Where(kv => !kv.Key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) && LoopbackPortOf(kv.Value) is { } p && RepairService.IsDeadLoopbackPort(p))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (deadKeys.Count > 0)
        {
            status = DiagStatus.Error;
            explanation = "settings.json 中的代理指向未在监听的端口，Claude Code 将无法连接（可能是 CCNest 异常退出后保留的设置）。";
            repair = RepairService.RemoveClaudeCodeProxy(code.SettingsPath, deadKeys, "移除失效的代理设置，Claude Code 将恢复原有联网方式。");
        }
        if (code.SettingsError != null)
        {
            status = DiagStatus.Warning;
            explanation = code.SettingsError;
        }
        if (code.ManagedProxyEnv.Count > 0)
        {
            status = DiagStatus.Warning;
            explanation = (explanation ?? "") + " 检测到组织托管策略中设置了代理，它的优先级高于用户设置，CCNest 无法覆盖。";
        }
        add(new DiagnosticItem
        {
            Id = "claude-code", Category = DiagCategory.Claude, Title = "Claude Code", Status = status,
            Detected = code.Installed ? $"已安装（{code.InstallKind}）{code.Version}" : "未检测到 Claude Code",
            Explanation = explanation, Repair = repair, Details = details,
        });

        var desktop = await ClaudeDesktopManager.DetectAsync(ct).ConfigureAwait(false);
        add(new DiagnosticItem
        {
            Id = "claude-desktop", Category = DiagCategory.Claude, Title = "Claude Desktop", Status = desktop.Installed ? DiagStatus.Ok : DiagStatus.Info,
            Detected = desktop.Installed ? $"已安装（{desktop.Kind} {desktop.Version}）{(desktop.RunningPids.Count > 0 ? "，正在运行" : "")}" : "未检测到 Claude Desktop",
            Explanation = desktop.Installed ? "Claude Desktop 基于 Electron，网络跟随 Windows 系统代理；由 CCNest 启动时会额外附带 --proxy-server 参数。" : null,
            Uncertainty = desktop.Installed ? "Claude Desktop 内的云端功能（如云端代码执行）运行在 Anthropic 服务器上，不受本机网络设置影响。" : null,
        });
    }

    private static async Task CheckRegionalAsync(AppSettings s, Action<DiagnosticItem> add, CancellationToken ct)
    {
        var r = await RegionalSettings.ReadStatusAsync(ct).ConfigureAwait(false);
        var details = new List<KeyValuePair<string, string>>
        {
            new("区域格式", r.Culture),
            new("国家或地区", $"{r.GeoName}（{r.GeoId}）"),
            new("首选语言", string.Join(", ", r.LanguageTags)),
            new("显示语言", r.UiLanguageOverride != null ? $"{r.UiLanguageOverride}（覆盖）" : r.CurrentUiLanguage),
            new("已安装显示语言", string.Join(", ", r.InstalledUiLanguages)),
            new("时区", r.TimeZoneDisplay),
            new("自动设置时区", r.AutoTimeZoneEnabled ? "开启" : "关闭"),
        };
        var diffs = new List<string>();
        if (s.SystemEnv.RegionalFormat && !r.Culture.Equals(s.SystemEnv.RegionalFormatLocale, StringComparison.OrdinalIgnoreCase)) diffs.Add($"区域格式为 {r.Culture}");
        if (s.SystemEnv.LanguageList && !string.Equals(r.LanguageTags.FirstOrDefault(), s.SystemEnv.PreferredLanguage, StringComparison.OrdinalIgnoreCase)) diffs.Add($"首选语言首位为 {r.LanguageTags.FirstOrDefault()}");
        if (s.SystemEnv.TimeZone && !r.TimeZoneId.Equals(s.SystemEnv.TimeZoneId, StringComparison.OrdinalIgnoreCase)) diffs.Add($"时区为 {r.TimeZoneId}");
        add(new DiagnosticItem
        {
            Id = "regional", Category = DiagCategory.System, Title = "Windows 区域与语言", Status = DiagStatus.Info,
            Detected = diffs.Count == 0 ? $"{r.Culture}，{r.TimeZoneDisplay}" : "与“系统环境”中选择的配置不同：" + string.Join("；", diffs),
            Explanation = diffs.Count > 0 ? "可在“系统环境”页面预览并应用（可随时恢复）。" : null,
            Uncertainty = "网页可见的语言与时区来自浏览器自身设置和 Windows 时区；显示语言更改需要注销后才会生效。",
            Details = details,
        });
    }

    private static async Task CheckSystemDnsAsync(Action<DiagnosticItem> add)
    {
        var net = NetworkInspector.Capture();
        var servers = net.Interfaces.Where(i => i.Status == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(i => i.Dns.Select(d => $"{d}（{i.Name}）")).Distinct().ToList();
        var (addresses, error) = await NetworkInspector.SystemResolveAsync("claude.ai", TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        var fake = addresses.Any(NetworkInspector.IsFakeIp);
        add(new DiagnosticItem
        {
            Id = "dns-system", Category = DiagCategory.Dns, Title = "系统 DNS", Status = error != null ? DiagStatus.Warning : DiagStatus.Info,
            Detected = error != null ? $"本机解析 claude.ai 失败：{error}"
                : fake ? $"本机解析 claude.ai 得到虚拟地址 {string.Join(", ", addresses.Take(2))}（TUN fake-ip 模式接管了 DNS）"
                : $"本机解析 claude.ai → {string.Join(", ", addresses.Take(3))}",
            Explanation = "专属浏览器与 Claude Code 通过 CCNest 代理访问时，域名由二级代理在远端解析，不依赖本机 DNS。" +
                          (fake ? " fake-ip 说明有 TUN 软件在接管系统 DNS。" : ""),
            Details = servers.Select(sv => new KeyValuePair<string, string>("DNS 服务器", sv)).ToList(),
        });
    }

    private static async Task CheckDirectAsync(Action<DiagnosticItem> add, CancellationToken ct)
    {
        var v4 = await IpEchoService.ObserveAsync(Array.Empty<ProxyEndpoint>(), TimeSpan.FromSeconds(8), includeClaude: false, ct).ConfigureAwait(false);
        var tun = NetworkInspector.Capture().DefaultRouteV4?.LooksLikeTun == true;
        add(new DiagnosticItem
        {
            Id = "direct", Category = DiagCategory.Network, Title = "本机直连出口", Status = v4.AnySuccess ? DiagStatus.Info : DiagStatus.Warning,
            Detected = v4.AnySuccess ? $"不经 CCNest 时的出口 IP：{v4.ConsensusIp}" : "无法直接访问互联网（可能处于受限网络，或被阻断规则拦截）",
            Explanation = tun ? "当前默认路由被虚拟网卡接管，这里的“直连”实际经过了该 TUN 软件。" : "这是不遵循代理的程序会使用的出口。",
        });

        var v6 = await IpEchoService.ObserveIPv6Async(Array.Empty<ProxyEndpoint>(), TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        if (v6.Success)
            add(new DiagnosticItem
            {
                Id = "direct-v6", Category = DiagCategory.Network, Title = "本机 IPv6 出口", Status = DiagStatus.Info,
                Detected = $"IPv6 直连可用：{v6.Ip}",
                Explanation = "CCNest 的内核在非 TUN 模式下不使用 IPv6；全局 TUN 模式会接管 IPv6 流量并阻止其绕行。",
            });
    }

    private bool IsRouteCurrent(RouteContext route) => route.Protected
        ? route.SessionId != null && _session?.HasSession == true && _session.Journal?.SessionId == route.SessionId
            && _session.Status.State is not (SessionState.Inactive or SessionState.Activating or SessionState.Deactivating)
        : _session?.HasSession != true;

    private async Task<ChainTestResult?> CheckChainAsync(AppSettings s, RouteContext route, Action<DiagnosticItem> add, CancellationToken ct)
    {
        if (!IsRouteCurrent(route))
        {
            add(new DiagnosticItem { Id = "chain", Category = DiagCategory.Proxy, Title = "双重代理线路", Status = DiagStatus.Unknown,
                Detected = "环境状态已变化或正在切换，本次未检测线路，请在环境稳定后重新检测。" });
            return null;
        }

        ChainTestResult result;
        if (route.Protected)
        {
            // The active core owns its upstream credentials and the running configuration.
            result = await _session!.TestCurrentRouteAsync(lookupIntel: true, ct: ct).ConfigureAwait(false);
        }
        else
        {
            var second = SettingsStore.ResolveSecondStage(s);
            if (second.Validate() is { } err)
            {
                add(new DiagnosticItem { Id = "chain", Category = DiagCategory.Proxy, Title = "双重代理线路", Status = DiagStatus.Error, Detected = "二级代理配置无效：" + err });
                return null;
            }
            List<ProxyEndpoint>? first = s.FirstStage.Mode switch
            {
                FirstStageMode.ClashLocalPort => new() { new ProxyEndpoint { Protocol = s.FirstStage.LocalProtocol, Host = s.FirstStage.LocalHost, Port = s.FirstStage.LocalPort } },
                FirstStageMode.ManualProxy => new() { SettingsStore.ResolveManualFirstStage(s) },
                FirstStageMode.None => new(),
                _ => null,
            };
            if (first == null)
            {
                add(new DiagnosticItem { Id = "chain", Category = DiagCategory.Proxy, Title = "双重代理线路", Status = DiagStatus.Unknown,
                    Detected = "一级线路为导入节点，需要激活环境后才能检测完整线路" });
                return null;
            }

            if (EndpointHeuristics.Analyze(second) is { } hint)
                add(new DiagnosticItem
                {
                    Id = "endpoint-kind", Category = DiagCategory.Proxy, Title = "二级代理类型", Status = DiagStatus.Warning,
                    Detected = "该代理端点可能不是固定的静态 IP", Explanation = hint.Message,
                    Uncertainty = "根据用户名中的常见参数格式推测，请以服务商后台的产品类型为准。",
                });
            result = await ChainTester.RunAsync(first, second, s.SecondStage.ExpectedExitIp, null, lookupIntel: true, ct).ConfigureAwait(false);
        }

        if (!IsRouteCurrent(route))
        {
            add(new DiagnosticItem { Id = "chain", Category = DiagCategory.Proxy, Title = "双重代理线路", Status = DiagStatus.Unknown,
                Detected = "检测期间环境状态已变化，结果不再代表当前线路，请重新检测。" });
            return null;
        }
        var failure = result.FirstFailure;
        add(new DiagnosticItem
        {
            Id = "chain", Category = DiagCategory.Proxy, Title = route.Protected ? "当前激活线路" : "双重代理线路（原始配置）",
            Status = failure != null ? DiagStatus.Error : result.Steps.Any(st => st.Status == StepStatus.Warning) ? DiagStatus.Warning : DiagStatus.Ok,
            Detected = failure != null ? $"{failure.Title}：{failure.Detail}"
                : route.Protected ? $"经当前 CCNest 内核，二级出口 {result.ExitIp}（Claude 边缘看到 {result.ClaudeEdgeIp ?? "-"} / {result.ClaudeEdgeCountry ?? "-"}）"
                : $"一级出口 {result.FirstStageExitIp ?? "-"} → 二级出口 {result.ExitIp}（Claude 边缘看到 {result.ClaudeEdgeIp ?? "-"} / {result.ClaudeEdgeCountry ?? "-"}）",
            Explanation = route.Protected ? "本次检测当前激活配置；已保存但尚未重新激活的设置未参与检测。一级端口和二级认证不由界面进程单独连接。" : null,
            ManualSteps = failure?.Hint,
            Details = result.Steps.Select(st => new KeyValuePair<string, string>(st.Title, $"[{StepName(st.Status)}] {st.Detail}")).ToList(),
        });

        if (result.Intel is { } intel)
        {
            var cls = intel.Classification;
            var details = intel.Sources.Select(src => new KeyValuePair<string, string>(src.Name,
                src.Ok ? $"{src.CountryCode} {src.Region} {src.City} | {src.Asn} {src.Org ?? src.Isp} {(src.Type != null ? "| 类型 " + src.Type : "")}{(src.IsProxy == true ? " | 标记为代理" : "")}{(src.IsVpn == true ? " | 标记为 VPN" : "")}"
                       : $"查询失败：{src.Error}")).ToList();
            add(new DiagnosticItem
            {
                Id = "exit-intel", Category = DiagCategory.Network, Title = "二级出口 IP 信息",
                Status = cls == IpClass.Datacenter || intel.AnyProxyFlag || intel.CountryDisagreement ? DiagStatus.Warning : DiagStatus.Info,
                Detected = $"{intel.Ip}：{intel.CountryCode} {intel.Region} {intel.City}，{intel.Asn} {intel.Org}，网络类型：{IpIntel.Describe(cls)}",
                Explanation = cls == IpClass.Datacenter ? "数据库将该 IP 识别为数据中心网络。若你购买的是住宅/ISP 代理，请向服务商确认。" :
                              intel.CountryDisagreement ? "不同数据库对该 IP 的国家判断不一致，常见于新分配或刚迁移的地址。" : null,
                Uncertainty = "IP 类型与地理位置来自第三方数据库，可能滞后或不准确，仅供参考，不代表任何平台的判断。",
                Details = details,
            });
            if (route.Protected && IsRouteCurrent(route)) _session?.SetIntel(intel);
        }
        return result;
    }

    private async Task CheckDnsLeakAsync(AppSettings s, RouteContext context, ChainTestResult? chain, Action<DiagnosticItem> add, CancellationToken ct)
    {
        void Unavailable(string reason) => add(new DiagnosticItem
        {
            Id = "dns-leak", Category = DiagCategory.Dns, Title = "DNS 泄漏检测（专属线路）", Status = DiagStatus.Unknown,
            Detected = reason,
        });
        if (!IsRouteCurrent(context))
        {
            Unavailable("环境状态已变化或正在切换，本次未检测 DNS；请在环境稳定后重新检测。");
            return;
        }
        if (context.Protected && (_session!.Status.State != SessionState.Protected || !_session.Status.GateOpen))
        {
            Unavailable("当前专属线路处于阻断或不可用状态，本次未检测 DNS；检测不会打开闸门。");
            return;
        }
        if (chain?.Passed != true)
        {
            Unavailable("专属线路尚未通过本次检测，暂不检测 DNS。");
            return;
        }
        var route = new List<ProxyEndpoint>();
        if (context.Protected)
        {
            if (_session!.ProbePort is not { } probe)
            {
                Unavailable("当前内核的专属检测端口不可用，本次未检测 DNS。");
                return;
            }
            // MixedPort may route non-Claude domains to stage one in ClaudeOnly mode.
            route.Add(new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = probe });
        }
        else
        {
            if (s.FirstStage.Mode == FirstStageMode.ClashLocalPort)
                route.Add(new ProxyEndpoint { Protocol = s.FirstStage.LocalProtocol, Host = s.FirstStage.LocalHost, Port = s.FirstStage.LocalPort });
            else if (s.FirstStage.Mode == FirstStageMode.ManualProxy)
                route.Add(SettingsStore.ResolveManualFirstStage(s));
            route.Add(SettingsStore.ResolveSecondStage(s));
        }

        var result = await DnsLeakTest.RunAsync(route, ct).ConfigureAwait(false);
        if (!IsRouteCurrent(context) || context.Protected && (_session!.Status.State != SessionState.Protected || !_session.Status.GateOpen))
        {
            Unavailable("检测期间环境状态已变化，DNS 结果不再代表当前线路，请重新检测。");
            return;
        }
        if (!result.Available)
        {
            add(new DiagnosticItem
            {
                Id = "dns-leak", Category = DiagCategory.Dns, Title = "DNS 泄漏检测", Status = DiagStatus.Unknown,
                Detected = result.Error ?? "检测服务不可用", Uncertainty = "该检测依赖第三方服务 bash.ws，服务不可用时无法得出结论。",
            });
            return;
        }
        var countries = result.Resolvers.Select(r => r.Country).Where(c => c != null).Distinct().ToList();
        add(new DiagnosticItem
        {
            Id = "dns-leak", Category = DiagCategory.Dns, Title = "DNS 泄漏检测（专属线路）",
            Status = result.Resolvers.Count == 0 ? DiagStatus.Unknown : DiagStatus.Info,
            Detected = result.Resolvers.Count == 0 ? "未观测到解析服务器（可能被服务限流）"
                : $"经专属线路的域名由 {result.Resolvers.Count} 个解析服务器处理，位于：{string.Join("、", countries)}",
            Explanation = "这些解析服务器由二级代理一侧决定。若其中出现你本地运营商的服务器，说明存在 DNS 泄漏。",
            Uncertainty = "国家信息来自检测服务的 IP 数据库。",
            Details = result.Resolvers.Select(r => new KeyValuePair<string, string>(r.Ip, $"{r.Country} {r.Asn}")).ToList(),
        });
    }

    private async Task CheckClockAsync(RouteContext context, Action<DiagnosticItem> add, CancellationToken ct)
    {
        if (!IsRouteCurrent(context) || context.Protected && _session?.ProbePort == null)
        {
            add(new DiagnosticItem { Id = "clock", Category = DiagCategory.System, Title = "系统时间", Status = DiagStatus.Unknown,
                Detected = "环境状态已变化或专属检测端口不可用，本次未获取网络时间。" });
            return;
        }
        IReadOnlyList<ProxyEndpoint> route = context.Protected
            ? new[] { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = _session!.ProbePort!.Value } }
            : Array.Empty<ProxyEndpoint>();
        using var client = ChainHttp.Create(route, TimeSpan.FromSeconds(8));
        using var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://www.cloudflare.com/"), ct).ConfigureAwait(false);
        if (resp.Headers.Date is not { } date)
        {
            add(new DiagnosticItem { Id = "clock", Category = DiagCategory.System, Title = "系统时间", Status = DiagStatus.Unknown, Detected = "无法获取网络时间" });
            return;
        }
        var skew = DateTimeOffset.UtcNow - date;
        var abs = Math.Abs(skew.TotalSeconds);
        add(new DiagnosticItem
        {
            Id = "clock", Category = DiagCategory.System, Title = "系统时间", Status = abs > 120 ? DiagStatus.Warning : DiagStatus.Ok,
            Detected = abs > 120 ? $"本机时间与网络时间相差 {abs:F0} 秒" : $"本机时间准确（偏差 {abs:F0} 秒）",
            ManualSteps = abs > 120 ? "时间偏差过大可能导致 TLS 证书错误和登录失败。请在 Windows 设置 › 时间和语言 中点击“立即同步”。" : null,
        });
    }

    private static void CheckBrowserEnvironment(AppSettings s, BrowserCheckResult r, ChainTestResult? chain, Action<DiagnosticItem> add)
    {
        var expected = s.SecondStage.ExpectedExitIp ?? chain?.ExitIp;
        var details = new List<KeyValuePair<string, string>>
        {
            new("语言", string.Join(", ", r.Languages)),
            new("时区", $"{r.TimeZone}（UTC{(r.TzOffsetMinutes >= 0 ? "+" : "")}{r.TzOffsetMinutes / 60.0}）"),
            new("区域格式", r.Locale ?? "-"),
            new("浏览器", r.Brands ?? r.UserAgent ?? "-"),
            new("浏览器出口 IP", r.BrowserExitIp ?? "获取失败"),
        };

        var exitOk = r.BrowserExitIp != null && expected != null && IpEchoService.SameIp(r.BrowserExitIp, expected);
        add(new DiagnosticItem
        {
            Id = "browser-exit", Category = DiagCategory.Browser, Title = "专属浏览器出口",
            Status = r.BrowserExitIp == null ? DiagStatus.Error : expected == null ? DiagStatus.Info : exitOk ? DiagStatus.Ok : DiagStatus.Error,
            Detected = r.BrowserExitIp == null ? "浏览器无法联网（环境未激活时这是预期行为）"
                : exitOk ? $"浏览器出口 {r.BrowserExitIp}，与期望一致" : $"浏览器出口 {r.BrowserExitIp}，期望 {expected ?? "未设置"}",
            Details = details,
        });

        var webrtcPublic = r.WebRtcPublicIps;
        var leaked = webrtcPublic.Where(ip => expected == null || !IpEchoService.SameIp(ip, expected)).ToList();
        add(new DiagnosticItem
        {
            Id = "webrtc", Category = DiagCategory.Browser, Title = "WebRTC 暴露",
            Status = leaked.Count > 0 ? DiagStatus.Error : DiagStatus.Ok,
            Detected = leaked.Count > 0 ? $"WebRTC 暴露了出口以外的公网 IP：{string.Join(", ", leaked)}"
                : webrtcPublic.Count == 0 ? "WebRTC 没有暴露任何公网 IP（非代理 UDP 已禁用）" : "WebRTC 仅暴露与出口一致的地址",
            Explanation = leaked.Count > 0 ? "请在 专属浏览器 设置中开启“限制 WebRTC 仅走代理”，并重新打开专属浏览器。" : null,
            Details = r.WebRtcLocalIps.Select(ip => new KeyValuePair<string, string>("本地地址", ip)).ToList(),
        });

        var winTz = TimeZoneInfo.Local;
        var browserOffset = r.TzOffsetMinutes ?? 0;
        var winOffset = (int)winTz.GetUtcOffset(DateTime.Now).TotalMinutes;
        var langOk = r.Languages.FirstOrDefault()?.StartsWith(s.Browser.AcceptLanguages.Split(',')[0], StringComparison.OrdinalIgnoreCase) == true;
        add(new DiagnosticItem
        {
            Id = "browser-locale", Category = DiagCategory.Browser, Title = "浏览器语言与时区", Status = DiagStatus.Info,
            Detected = $"语言首选 {r.Languages.FirstOrDefault() ?? "-"}{(langOk ? "（与设置一致）" : $"（设置为 {s.Browser.AcceptLanguages}）")}；时区 {r.TimeZone}{(browserOffset == winOffset ? "（与 Windows 一致）" : "（与 Windows 不一致）")}",
            Explanation = "浏览器时区来自 Windows 时区设置；语言来自专属浏览器自己的语言偏好。这里只陈述事实，不评估任何“风险分数”。",
        });
    }

    // ---------------------------------------------------------------------------------------------

    internal static int? LoopbackPortOf(string value)
    {
        var v = value.Trim();
        if (!v.Contains("://")) v = "http://" + v;
        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)) return null;
        return uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1" && uri.Port > 0 ? uri.Port : null;
    }

    private static string OnOff(bool? b) => b == null ? "未知" : b.Value ? "开" : "关";
    private static string Svc(ServiceState s) => s switch
    {
        ServiceState.Running => "运行中",
        ServiceState.Stopped => "已停止",
        ServiceState.NotInstalled => "未安装",
        _ => s.ToString(),
    };
    private static string ScopeName(EnvScope s) => s switch { EnvScope.User => "用户", EnvScope.Machine => "系统", _ => "当前进程" };
    private static string StepName(StepStatus s) => s switch
    {
        StepStatus.Passed => "通过", StepStatus.Warning => "注意", StepStatus.Failed => "失败", StepStatus.Skipped => "跳过", _ => s.ToString(),
    };
}
