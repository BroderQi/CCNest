using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Claude;
using CCNest.Core.Proxy;
using CCNest.Core.Session;
using CCNest.Core.Settings;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    public DashboardViewModel(AppServices services, MainViewModel main) : base(services)
    {
        _main = main;
        _status = services.Session.Status;
        services.Session.StatusChanged += s => Ui(() => Status = s);
        services.Settings.Saved += _ => Ui(RefreshSummary);
        RefreshSummary();
        _clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            if (Status.Since != null) OnPropertyChanged(nameof(SinceText));
        };
        _clock.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _clock;

    public override string Key => "dashboard";
    public override string Title => "概览";
    public override string Subtitle => "一键激活 Claude 专属环境，结束时恢复原始设置。";
    public override SymbolRegular Icon => SymbolRegular.Home24;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsInactive), nameof(IsTransitioning), nameof(StateLabel), nameof(HasMismatch), nameof(SinceText), nameof(LatencyText), nameof(IntelText))]
    private SessionStatusInfo _status;

    public bool IsActive => Status.State is SessionState.Protected or SessionState.Blocked or SessionState.Degraded;
    public bool IsInactive => Status.State == SessionState.Inactive;
    public bool IsTransitioning => Status.State is SessionState.Activating or SessionState.Deactivating;
    public bool HasMismatch => Status.State == SessionState.Blocked && Status.ExitIp != null && Status.ExpectedIp != null && Status.ExitIp != Status.ExpectedIp;

    public string StateLabel => Status.State switch
    {
        SessionState.Protected => "受保护",
        SessionState.Blocked => "已阻断",
        SessionState.Degraded => "线路中断",
        SessionState.Activating => "正在激活",
        SessionState.Deactivating => "正在恢复",
        _ => "未激活",
    };

    public string SinceText => Status.Since is { } s ? $"已运行 {(DateTime.Now - s):hh\\:mm\\:ss}" : "";
    public string LatencyText => Status.LatencyMs is { } l ? $"{l} ms" : "—";
    public string IntelText => Status.Intel is { } i
        ? $"{i.CountryCode} {i.City} · {i.Org} · {IpIntel.Describe(i.Classification)}"
        : Status.ExitCountry is { } c ? $"Claude 边缘节点显示地区：{c}" : "";

    public ObservableCollection<ActivationStep> Steps { get; } = new();
    public ObservableCollection<string> Notices { get; } = new();
    public ObservableCollection<ProtectionScopeItem> Scope { get; } = new();

    [ObservableProperty] private string _firstStageText = "";
    [ObservableProperty] private string _secondStageText = "";
    [ObservableProperty] private string _protectionText = "";
    [ObservableProperty] private string? _expectedIpText;
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _showSteps;

    public override Task OnNavigatedToAsync()
    {
        RefreshSummary();
        return Task.CompletedTask;
    }

    private void RefreshSummary()
    {
        var s = Services.Settings.Current;
        FirstStageText = s.FirstStage.Mode switch
        {
            FirstStageMode.ClashLocalPort => $"现有 Clash / Mihomo · 127.0.0.1:{s.FirstStage.LocalPort}{(s.FirstStage.LockNode is { Length: > 0 } n ? $" · 锁定节点 {n}" : "")}",
            FirstStageMode.ManualProxy => $"手动代理 · {s.FirstStage.Manual}",
            FirstStageMode.ImportedNode => $"导入节点 · {s.FirstStage.ImportedNodeName} ({s.FirstStage.ImportedNodeType})",
            _ => "不使用一级代理",
        };
        IsConfigured = !string.IsNullOrWhiteSpace(s.SecondStage.Endpoint.Host);
        SecondStageText = IsConfigured ? $"{s.SecondStage.DisplayName} · {s.SecondStage.Endpoint}" : "尚未配置";
        ExpectedIpText = s.SecondStage.ExpectedExitIp;
        ProtectionText = s.Protection.Level switch
        {
            ProtectionLevel.Standard => "标准模式（无需管理员）",
            ProtectionLevel.AppEnforced => "应用强制模式（WFP，需要管理员）",
            _ => "全局 TUN 模式（WFP 防泄漏，需要管理员）",
        } + (s.Protection.Scope == RoutingScope.ClaudeOnly ? " · 仅 Claude 域名" : " · 全部流量");
        Scope.Clear();
        foreach (var item in SessionManager.DescribeScope(s)) Scope.Add(item);
    }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (Services.Session.Journal != null) return;
        var s = Services.Settings.Current;
        if (!IsConfigured)
        {
            await Services.Dialogs.ShowMessageAsync("尚未配置二级代理", "请先在“网络线路”页面填写你购买的静态代理信息并测试线路。");
            _main.Navigate("network");
            return;
        }

        if (!s.Ui.SkipActivationPreview && !await ConfirmActivationAsync(s)) return;

        Steps.Clear();
        Notices.Clear();
        ShowSteps = true;
        var progress = new Progress<ActivationStep>(step => Steps.Add(step));
        var result = await Services.Session.ActivateAsync(progress);
        foreach (var n in result.Notices) Notices.Add(n);
        if (result.Success)
        {
            Services.Dialogs.ShowSuccessToast("Claude 专属环境已激活");
            if (result.ChainTest?.ExitIp is { } ip && string.IsNullOrEmpty(s.SecondStage.ExpectedExitIp))
                Services.Settings.Update(x => x.SecondStage.ExpectedExitIp = ip);
            _ = LookupIntelAsync();
        }
        else
        {
            await Services.Dialogs.ShowMessageAsync("激活失败", result.Message + "\n\n未对系统做任何保留修改（已自动回滚）。可在“环境检测”中查看详细原因。");
        }
    }

    private async Task<bool> ConfirmActivationAsync(AppSettings s)
    {
        var changes = new List<ChangePreview>
        {
            new("启动 CCNest 专属代理内核", $"本地端口 127.0.0.1:{s.Advanced.MixedPort}，经 一级线路 → 二级静态代理 出网", "立即生效"),
        };
        if (s.Integrations.SetSystemProxy)
            changes.Add(new("Windows 系统代理", $"指向 127.0.0.1:{s.Advanced.MixedPort}（结束时恢复原设置）", "立即生效（遵循系统代理的程序）"));
        if (s.Integrations.ConfigureClaudeCode)
            changes.Add(new("Claude Code 代理配置", $"在 {ClaudeCodeManager.SettingsPath} 的 env 中加入 HTTPS_PROXY / HTTP_PROXY", "新启动的 Claude Code 生效"));
        if (s.Integrations.SetUserEnvironmentProxy)
            changes.Add(new("用户环境变量", "HTTPS_PROXY / HTTP_PROXY / NO_PROXY", "新启动的程序生效"));
        if (s.Protection.Level == ProtectionLevel.AppEnforced)
            changes.Add(new("Claude 应用联网限制（需要管理员）", "Claude Desktop / Claude Code 只能通过 CCNest 联网", "立即生效"));
        if (s.Protection.Level == ProtectionLevel.SystemTun)
            changes.Add(new("全局 TUN 与防泄漏阻断（需要管理员）", "创建 CCNest 虚拟网卡接管系统流量；绕过它的流量将被 WFP 阻断" + (s.Integrations.DisableExistingClientTun ? "；如现有 Clash 开启了 TUN 会临时关闭" : ""), "立即生效"));
        if (s.SystemEnv.ApplyWithSession)
            changes.Add(new("Windows 区域与语言", "按“系统环境”页面的选择应用，结束时恢复", "多数立即生效；显示语言需注销"));
        var notes = new List<string>();
        if (s.Protection.CrashPolicy == CrashPolicy.KeepBlocked)
            notes.Add("崩溃策略为“安全优先”：如果 CCNest 意外退出，上述设置会保持，Claude 流量被阻断而不是改走其他出口，直到你重新打开 CCNest 选择恢复。");
        var ok = await Services.Dialogs.ConfirmChangesAsync("激活 Claude 专属环境", "将进行以下修改：", changes, notes, "激活");
        return ok;
    }

    private async Task LookupIntelAsync()
    {
        try
        {
            if (Services.Session.ProbePort is not { } port || Status.ExitIp is not { } ip) return;
            var intel = await IpIntelService.LookupAsync(ip, new[] { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = port } }, TimeSpan.FromSeconds(10));
            Services.Session.SetIntel(intel);
        }
        catch { /* optional */ }
    }

    [RelayCommand]
    private async Task DeactivateAsync()
    {
        var result = await Services.Session.DeactivateAsync();
        ShowSteps = false;
        Notices.Clear();
        foreach (var n in result.Notices) Notices.Add(n);
        if (!result.Success || result.NeedsSignOut || result.Outcomes.Any(o => o.Change.State == ChangeState.Conflict))
        {
            var lines = result.Outcomes.Select(o => $"{(o.Success ? "✓" : o.Change.State == ChangeState.Conflict ? "!" : "✗")} {o.Change.Title}：{o.Message}");
            await Services.Dialogs.ShowMessageAsync("恢复报告", string.Join("\n", lines) + (result.NeedsSignOut ? "\n\n部分设置需要注销并重新登录 Windows 后才会完全恢复。" : ""));
        }
        else
        {
            Services.Dialogs.ShowSuccessToast("已结束会话，原始设置已恢复");
        }
    }

    [RelayCommand]
    private async Task ReverifyAsync()
    {
        var ok = await Services.Session.ReverifyAsync("手动复核");
        if (ok) Services.Dialogs.ShowSuccessToast("出口已确认，流量已恢复");
    }

    [RelayCommand]
    private async Task AcceptNewIpAsync()
    {
        if (Status.ExitIp is not { } ip) return;
        var ok = await Services.Dialogs.ConfirmAsync("接受新的出口 IP",
            $"将期望出口 IP 更新为 {ip} 并恢复流量。\n\n请确认这是服务商为你分配的 IP（例如你在后台更换了 IP）。如果不确定，请先联系服务商。", "接受并恢复");
        if (ok && await Services.Session.AcceptNewExitIpAsync(ip)) Services.Dialogs.ShowSuccessToast($"期望出口已更新为 {ip}");
    }

    [RelayCommand]
    private Task OpenBrowserAsync() => _main.OpenBrowserAsync();

    [RelayCommand]
    private void OpenClaudeTerminal()
    {
        if (Services.Session.MixedPort is not { } port || Services.Session.Journal == null)
        {
            Services.Dialogs.ShowToast("需要先激活", "激活 Claude 环境后才能打开 Claude Code 专属终端。");
            return;
        }
        ClaudeCodeManager.LaunchTerminal(port);
    }

    [RelayCommand]
    private async Task LaunchDesktopAsync()
    {
        if (Services.Session.MixedPort is not { } port || Services.Session.Journal == null)
        {
            Services.Dialogs.ShowToast("需要先激活", "激活 Claude 环境后才能通过 CCNest 启动 Claude Desktop。");
            return;
        }
        var info = await ClaudeDesktopManager.DetectAsync();
        var error = ClaudeDesktopManager.LaunchWithProxy(info, port);
        if (error != null) await Services.Dialogs.ShowMessageAsync("Claude Desktop", error);
    }

    [RelayCommand]
    private void Go(string page) => _main.Navigate(page);
}
