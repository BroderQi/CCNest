using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Infrastructure;
using CCNest.Core.Mihomo;
using CCNest.Core.Proxy;
using CCNest.Core.Session;
using CCNest.Core.Settings;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed record ProviderOption(string Id, string Name, string Hint);

public sealed partial class NetworkViewModel : PageViewModel
{
    public NetworkViewModel(AppServices services) : base(services)
    {
        Load();
        services.Settings.Saved += _ => Ui(() =>
        {
            if (!IsDirty) Load();
        });
    }

    public override string Key => "network";
    public override string Title => "网络线路";
    public override string Subtitle => "配置 一级线路（现有 Clash / Mihomo）→ 二级静态代理 的双重线路，并验证真实出口。";
    public override SymbolRegular Icon => SymbolRegular.Globe24;

    public static IReadOnlyList<ProviderOption> Providers { get; } = new[]
    {
        new ProviderOption("custom", "其他 / 自定义", "填写服务商提供的静态代理信息。支持粘贴 host:port:用户名:密码、用户名:密码@host:port 或 socks5:// / http:// 链接。"),
        new ProviderOption("iproyal", "IPRoyal", "静态住宅（ISP）代理一般以 IP:端口:用户名:密码 提供，可选 HTTP 或 SOCKS5。直接粘贴整行即可。"),
        new ProviderOption("decodo", "Decodo（原 Smartproxy）", "在控制台的静态住宅 / ISP 代理页面复制端点（host:port:用户名:密码）。使用 SOCKS5 前请确认控制台中已启用。"),
        new ProviderOption("oxylabs", "Oxylabs", "ISP / 专属代理使用控制台给出的入口地址与端口（通常每个端口对应一个固定 IP），用户名形如 user-xxxx。"),
        new ProviderOption("cliproxy", "Cliproxy", "按后台列出的静态 IP 条目填写，常见格式为 IP:端口:用户名:密码。"),
    };

    public static IReadOnlyList<ProxyProtocol> Protocols { get; } = new[] { ProxyProtocol.Socks5, ProxyProtocol.Http };

    // ---------------- first stage ----------------
    [ObservableProperty] private FirstStageMode _firstMode;
    [ObservableProperty] private int _localPort = 7897;
    [ObservableProperty] private ProxyProtocol _localProtocol = ProxyProtocol.Socks5;
    [ObservableProperty] private string? _controllerAddress;
    [ObservableProperty] private string? _controllerSecret;
    [ObservableProperty] private string? _lockGroup;
    [ObservableProperty] private string? _lockNode;
    [ObservableProperty] private string _manualText = "";
    [ObservableProperty] private ProxyProtocol _manualProtocol = ProxyProtocol.Socks5;
    [ObservableProperty] private string _importYaml = "";
    [ObservableProperty] private string? _importedNodeSummary;
    [ObservableProperty] private string? _selectedConfigNode;

    public ObservableCollection<string> GroupNames { get; } = new();
    public ObservableCollection<string> GroupMembers { get; } = new();
    public ObservableCollection<string> ConfigNodeNames { get; } = new();
    public ObservableCollection<int> DetectedPorts { get; } = new();

    [ObservableProperty] private ClashEnvironment? _clash;
    [ObservableProperty] private string _clashSummary = "尚未检测";
    [ObservableProperty] private bool _clashFound;

    // ---------------- second stage ----------------
    [ObservableProperty] private ProviderOption _provider = Providers[0];
    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private ProxyProtocol _secondProtocol = ProxyProtocol.Socks5;
    [ObservableProperty] private string _secondHost = "";
    [ObservableProperty] private int _secondPort = 1080;
    [ObservableProperty] private string _secondUser = "";
    [ObservableProperty] private string _secondPassword = "";
    [ObservableProperty] private string _displayName = "静态住宅代理";
    [ObservableProperty] private string _expectedIp = "";
    [ObservableProperty] private string? _endpointWarning;

    partial void OnSecondUserChanged(string value) =>
        EndpointWarning = EndpointHeuristics.Analyze(new ProxyEndpoint { Host = "x", Port = 1, Username = value })?.Message;

    // ---------------- protection ----------------
    [ObservableProperty] private ProtectionLevel _level;
    [ObservableProperty] private RoutingScope _scope;
    [ObservableProperty] private CrashPolicy _crashPolicy;
    [ObservableProperty] private bool _blockOnMismatch;
    [ObservableProperty] private bool _reverify;
    [ObservableProperty] private bool _allowLan;
    [ObservableProperty] private bool _requireExpectedIp;
    [ObservableProperty] private bool _disableExistingTun;

    // ---------------- test ----------------
    public ObservableCollection<ChainStep> TestSteps { get; } = new();
    [ObservableProperty] private ChainTestResult? _testResult;
    [ObservableProperty] private string? _testSummary;
    [ObservableProperty] private string? _intelSummary;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string? _validationError;

    public bool SessionActive => Services.Session.Journal != null;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsDirty) or nameof(IsBusy) or nameof(BusyText) or nameof(TestResult) or nameof(TestSummary) or nameof(IntelSummary)
            or nameof(ValidationError) or nameof(Clash) or nameof(ClashSummary) or nameof(ClashFound) or nameof(PasteText) or nameof(ImportedNodeSummary)
            or nameof(SelectedConfigNode) or nameof(ImportYaml) or nameof(ManualText) or nameof(SessionActive) or nameof(EndpointWarning)) return;
        IsDirty = true;
    }

    partial void OnLockGroupChanged(string? value)
    {
        GroupMembers.Clear();
        var group = Clash?.Groups.FirstOrDefault(g => g.Name == value);
        if (group != null) foreach (var m in group.Members) GroupMembers.Add(m);
    }

    public override async Task OnNavigatedToAsync()
    {
        OnPropertyChanged(nameof(SessionActive));
        if (Clash == null) await DetectAsync();
    }

    private void Load()
    {
        var s = Services.Settings.Current;
        FirstMode = s.FirstStage.Mode;
        LocalPort = s.FirstStage.LocalPort;
        LocalProtocol = s.FirstStage.LocalProtocol;
        ControllerAddress = s.FirstStage.ControllerAddress;
        LockGroup = s.FirstStage.LockGroup;
        LockNode = s.FirstStage.LockNode;
        ManualProtocol = s.FirstStage.Manual.Protocol;
        ManualText = string.IsNullOrEmpty(s.FirstStage.Manual.Host) ? "" : $"{s.FirstStage.Manual.Host}:{s.FirstStage.Manual.Port}" +
                     (s.FirstStage.Manual.HasCredentials ? $":{s.FirstStage.Manual.Username}:{SecretStore.Get(SecretStore.FirstStagePassword)}" : "");
        ImportedNodeSummary = s.FirstStage.ImportedNodeName != null ? $"已导入：{s.FirstStage.ImportedNodeName}（{s.FirstStage.ImportedNodeType}）" : null;

        Provider = Providers.FirstOrDefault(p => p.Id == s.SecondStage.Provider) ?? Providers[0];
        SecondProtocol = s.SecondStage.Endpoint.Protocol;
        SecondHost = s.SecondStage.Endpoint.Host;
        SecondPort = s.SecondStage.Endpoint.Port == 0 ? 1080 : s.SecondStage.Endpoint.Port;
        SecondUser = s.SecondStage.Endpoint.Username ?? "";
        SecondPassword = SecretStore.Get(SecretStore.SecondStagePassword) ?? "";
        DisplayName = s.SecondStage.DisplayName;
        ExpectedIp = s.SecondStage.ExpectedExitIp ?? "";

        Level = s.Protection.Level;
        Scope = s.Protection.Scope;
        CrashPolicy = s.Protection.CrashPolicy;
        BlockOnMismatch = s.Protection.BlockOnExitMismatch;
        Reverify = s.Protection.ReverifyOnWakeOrNetworkChange;
        AllowLan = s.Protection.AllowLan;
        RequireExpectedIp = s.Protection.RequireExpectedIp;
        DisableExistingTun = s.Integrations.DisableExistingClientTun;
        IsDirty = false;
    }

    [RelayCommand]
    private Task DetectAsync() => RunBusyAsync("正在检测 Clash / Mihomo…", async () =>
    {
        var env = await ClashDetector.DetectAsync(string.IsNullOrWhiteSpace(ControllerAddress) ? null : ControllerAddress,
            string.IsNullOrWhiteSpace(ControllerSecret) ? SecretStore.Get(SecretStore.ClashControllerSecret) : ControllerSecret);
        Clash = env;
        Services.LastClash = env;
        ClashFound = env.Found;
        DetectedPorts.Clear();
        foreach (var p in env.Ports) DetectedPorts.Add(p.Port);
        var dirty = IsDirty;
        GroupNames.Clear();
        foreach (var g in env.Groups.Where(g => g.Type == "Selector")) GroupNames.Add(g.Name);
        if (LockGroup != null) OnLockGroupChanged(LockGroup);

        ConfigNodeNames.Clear();
        foreach (var file in env.ConfigFiles.Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            var cfg = ClashConfigReader.TryRead(file);
            if (cfg == null) continue;
            foreach (var n in cfg.ProxyNames) if (!ConfigNodeNames.Contains(n)) ConfigNodeNames.Add(n);
        }

        if (env.Found)
        {
            ClashSummary = $"{env.ClientName}" + (env.CoreVersion != null ? $" · mihomo {env.CoreVersion}" : "") +
                           $" · 端口 {string.Join(", ", env.Ports.Select(p => p.Port))}" +
                           (env.TunEnabled == true ? " · TUN 已开启" : "");
            if (env.RecommendedPort is { } rp && FirstMode == FirstStageMode.ClashLocalPort && !env.Ports.Any(p => p.Port == LocalPort))
            {
                LocalPort = rp.Port;
                LocalProtocol = rp.Socks5 ? ProxyProtocol.Socks5 : ProxyProtocol.Http;
                dirty = true;
            }
            if (env.ControllerReachable && env.ControllerSecret != null) SecretStore.Set(SecretStore.ClashControllerSecret, env.ControllerSecret);
        }
        else
        {
            ClashSummary = "未检测到正在运行的 Clash / Mihomo 客户端";
        }
        IsDirty = dirty;
    });

    partial void OnProviderChanged(ProviderOption value) => OnPropertyChanged(nameof(ProviderHint));
    public string ProviderHint => Provider.Hint;

    [RelayCommand]
    private void ParsePaste()
    {
        if (!ProxyEndpoint.TryParse(PasteText, SecondProtocol, out var ep, out var error))
        {
            ValidationError = error;
            return;
        }
        ValidationError = null;
        SecondProtocol = ep.Protocol;
        SecondHost = ep.Host;
        SecondPort = ep.Port;
        SecondUser = ep.Username ?? "";
        SecondPassword = ep.Password ?? "";
        PasteText = "";
        Services.Dialogs.ShowToast("已识别", $"{ep}（请确认协议类型是否正确）");
    }

    [RelayCommand]
    private void UseDetectedIp()
    {
        if (TestResult?.ExitIp is { } ip) ExpectedIp = ip;
    }

    [RelayCommand]
    private void ImportFromConfig()
    {
        if (SelectedConfigNode == null || Clash == null) return;
        foreach (var file in Clash.ConfigFiles)
        {
            try
            {
                var yaml = ClashConfigReader.ExtractProxyYaml(File.ReadAllText(file), SelectedConfigNode);
                if (yaml == null) continue;
                ImportYaml = yaml;
                ImportNode();
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("Network", ex.Message);
            }
        }
        Services.Dialogs.ShowErrorToast("未能在配置文件中找到该节点");
    }

    [RelayCommand]
    private void ImportNode()
    {
        try
        {
            var node = MihomoConfigBuilder.NodeFromYaml(ImportYaml);
            var name = node["name"]?.ToString() ?? "节点";
            var type = node["type"]?.ToString() ?? "?";
            // Validate by building a throw-away config.
            MihomoConfigBuilder.Build(new CoreConfigSpec
            {
                MixedPort = 1, ProbeChainPort = 2, ProbeFirstPort = 3, ControllerPort = 4, Secret = new string('x', 32),
                FirstStageMode = FirstStageMode.ImportedNode, FirstStageNode = node,
                SecondStage = new ProxyEndpoint { Host = "example.com", Port = 1 },
            });
            SecretStore.Set(SecretStore.FirstStageNodeYaml, ImportYaml);
            Services.Settings.Update(s =>
            {
                s.FirstStage.ImportedNodeName = name;
                s.FirstStage.ImportedNodeType = type;
            });
            ImportedNodeSummary = $"已导入：{name}（{type}）";
            ImportYaml = "";
            Services.Dialogs.ShowSuccessToast($"已导入节点 {name}（凭据已加密保存）");
        }
        catch (Exception ex)
        {
            ValidationError = "节点无效：" + ex.Message;
        }
    }

    private bool TryBuild(out AppSettings settings, out string? error)
    {
        settings = SettingsStore.Clone(Services.Settings.Current);
        error = null;
        var second = new ProxyEndpoint
        {
            Protocol = SecondProtocol,
            Host = SecondHost.Trim(),
            Port = SecondPort,
            Username = string.IsNullOrWhiteSpace(SecondUser) ? null : SecondUser.Trim(),
            Password = string.IsNullOrEmpty(SecondPassword) ? null : SecondPassword,
        };
        error = second.Validate();
        if (error != null) { error = "二级代理：" + error; return false; }
        if (!string.IsNullOrWhiteSpace(ExpectedIp) && !System.Net.IPAddress.TryParse(ExpectedIp.Trim(), out _))
        {
            error = "期望出口 IP 格式无效";
            return false;
        }

        ProxyEndpoint manual = settings.FirstStage.Manual;
        string? manualPassword = null;
        if (FirstMode == FirstStageMode.ManualProxy)
        {
            if (!ProxyEndpoint.TryParse(ManualText, ManualProtocol, out manual, out var e))
            {
                error = "一级手动代理：" + e;
                return false;
            }
            manualPassword = manual.Password;
        }
        if (FirstMode == FirstStageMode.ClashLocalPort && LocalPort is < 1 or > 65535)
        {
            error = "一级端口无效";
            return false;
        }
        if (FirstMode == FirstStageMode.ImportedNode && !SecretStore.Has(SecretStore.FirstStageNodeYaml))
        {
            error = "请先导入一级节点";
            return false;
        }

        settings.FirstStage.Mode = FirstMode;
        settings.FirstStage.LocalPort = LocalPort;
        settings.FirstStage.LocalProtocol = LocalProtocol;
        settings.FirstStage.ControllerAddress = string.IsNullOrWhiteSpace(ControllerAddress) ? null : ControllerAddress.Trim();
        settings.FirstStage.LockGroup = string.IsNullOrWhiteSpace(LockGroup) ? null : LockGroup;
        settings.FirstStage.LockNode = string.IsNullOrWhiteSpace(LockNode) ? null : LockNode;
        settings.FirstStage.Manual = manual with { Password = null };
        settings.SecondStage.Provider = Provider.Id;
        settings.SecondStage.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "静态住宅代理" : DisplayName.Trim();
        settings.SecondStage.Endpoint = second with { Password = null };
        settings.SecondStage.ExpectedExitIp = string.IsNullOrWhiteSpace(ExpectedIp) ? null : ExpectedIp.Trim();
        settings.Protection.Level = Level;
        settings.Protection.Scope = Scope;
        settings.Protection.CrashPolicy = CrashPolicy;
        settings.Protection.BlockOnExitMismatch = BlockOnMismatch;
        settings.Protection.ReverifyOnWakeOrNetworkChange = Reverify;
        settings.Protection.AllowLan = AllowLan;
        settings.Protection.RequireExpectedIp = RequireExpectedIp;
        settings.Integrations.DisableExistingClientTun = DisableExistingTun;

        // Secrets are stored separately (DPAPI), never in settings.json.
        SecretStore.Set(SecretStore.SecondStagePassword, second.Password);
        if (FirstMode == FirstStageMode.ManualProxy) SecretStore.Set(SecretStore.FirstStagePassword, manualPassword);
        if (!string.IsNullOrWhiteSpace(ControllerSecret)) SecretStore.Set(SecretStore.ClashControllerSecret, ControllerSecret);
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryBuild(out var settings, out var error))
        {
            ValidationError = error;
            return;
        }
        ValidationError = null;
        Services.Settings.Save(settings);
        IsDirty = false;
        Services.Dialogs.ShowSuccessToast(SessionActive ? "设置已保存，将在下次激活时生效" : "设置已保存");
    }

    [RelayCommand]
    private void Revert() => Load();

    [RelayCommand]
    private Task TestAsync() => RunBusyAsync("正在逐级测试线路…", async () =>
    {
        if (!TryBuild(out var settings, out var error))
        {
            ValidationError = error;
            return;
        }
        ValidationError = null;
        Services.Settings.Save(settings);
        IsDirty = false;

        TestSteps.Clear();
        TestResult = null;
        TestSummary = null;
        IntelSummary = null;

        List<ProxyEndpoint>? first = settings.FirstStage.Mode switch
        {
            FirstStageMode.ClashLocalPort => new() { new ProxyEndpoint { Protocol = settings.FirstStage.LocalProtocol, Host = "127.0.0.1", Port = settings.FirstStage.LocalPort } },
            FirstStageMode.ManualProxy => new() { SettingsStore.ResolveManualFirstStage(settings) },
            FirstStageMode.None => new(),
            _ => null,
        };
        MihomoSidecar? temp = null;
        try
        {
            if (first == null)
            {
                // Imported node: start a temporary private core exposing the node on a local port.
                var ports = SidecarPorts.Allocate(Core.SystemEnv.NetworkInspector.GetFreePort(), needFirstProbe: true);
                var spec = new CoreConfigSpec
                {
                    MixedPort = ports.Mixed, ProbeChainPort = ports.ProbeChain, ProbeFirstPort = ports.ProbeFirst, ControllerPort = ports.Controller, Secret = ports.Secret,
                    FirstStageMode = FirstStageMode.ImportedNode,
                    FirstStageNode = MihomoConfigBuilder.NodeFromYaml(SecretStore.Get(SecretStore.FirstStageNodeYaml)!),
                    SecondStage = SettingsStore.ResolveSecondStage(settings), StartGateClosed = true,
                };
                temp = new MihomoSidecar(AppPaths.BundledMihomoPath, Path.Combine(AppPaths.RuntimeDir, "test-core"));
                await temp.StartAsync(MihomoConfigBuilder.Build(spec), ports);
                first = new() { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = ports.ProbeFirst!.Value } };
            }

            var progress = new Progress<ChainStep>(step => TestSteps.Add(step));
            var result = await ChainTester.RunAsync(first, SettingsStore.ResolveSecondStage(settings), settings.SecondStage.ExpectedExitIp, progress, lookupIntel: true);
            TestResult = result;
            foreach (var step in result.Steps)
                Log.Write(step.Status == StepStatus.Failed ? LogLevel.Warn : LogLevel.Info, "RouteTest", $"[{step.Status}] {step.Title}: {step.Detail}");
            TestSummary = result.Passed
                ? $"线路可用：一级出口 {result.FirstStageExitIp ?? "—"} → 二级出口 {result.ExitIp}，Claude 边缘看到 {result.ClaudeEdgeIp ?? "—"}（{result.ClaudeEdgeCountry ?? "—"}）"
                : $"线路未通过：{result.FirstFailure?.Title} — {result.FirstFailure?.Detail}";
            if (result.Intel is { } intel)
                IntelSummary = $"{intel.Ip} · {intel.CountryCode} {intel.Region} {intel.City} · {intel.Asn} {intel.Org} · 网络类型：{IpIntel.Describe(intel.Classification)}（数据库判断，仅供参考）";
            if (result.Passed && string.IsNullOrWhiteSpace(ExpectedIp) && result.ExitIp != null)
            {
                var use = await Services.Dialogs.ConfirmAsync("设为期望出口 IP？",
                    $"本次检测到的二级出口为 {result.ExitIp}。\n\n设为期望出口后，CCNest 会在会话期间持续核对，一旦出口变化将立即阻断流量。请确认它与服务商后台显示的 IP 一致。", "设为期望 IP", "暂不设置");
                if (use)
                {
                    ExpectedIp = result.ExitIp;
                    Save();
                }
            }
        }
        finally
        {
            if (temp != null) await temp.DisposeAsync();
        }
    });
}
