using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Browser;
using CCNest.Core.Diagnostics;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed partial class DiagnosticItemViewModel : ObservableObject
{
    public DiagnosticItemViewModel(DiagnosticItem item) => Item = item;

    public DiagnosticItem Item { get; }
    public string Title => Item.Title;
    public DiagStatus Status => Item.Status;
    public string Detected => Item.Detected;
    public string? Explanation => Item.Explanation;
    public string? ManualSteps => Item.ManualSteps;
    public string? Uncertainty => Item.Uncertainty;
    public RepairAction? Repair => Item.Repair;
    public bool HasDetails => Item.Details.Count > 0;
    public IReadOnlyList<KeyValuePair<string, string>> Details => Item.Details;
    public string StatusText => Item.Status switch
    {
        DiagStatus.Ok => "正常",
        DiagStatus.Info => "信息",
        DiagStatus.Warning => Item.ConsistencyWeight.HasValue ? "风险" : "注意",
        DiagStatus.Error => "需处理",
        _ => Item.ConsistencyWeight.HasValue ? "待检测" : "无法确定",
    };

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _repairResult;
}

public sealed record DiagnosticGroup(string Name, ObservableCollection<DiagnosticItemViewModel> Items);

public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private DiagnosticReport? _report;

    public DiagnosticsViewModel(AppServices services) : base(services) { }

    public override string Key => "diagnostics";
    public override string Title => "环境检测";
    public override string Subtitle => "比较浏览器、Windows 与 IP 出口的一致性，逐项显示正常、风险和待检测，并检查网络线路。";
    public override SymbolRegular Icon => SymbolRegular.Stethoscope24;

    public ObservableCollection<DiagnosticGroup> Groups { get; } = new();

    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _okCount;
    [ObservableProperty] private int _repairableCount;
    [ObservableProperty] private string? _lastRunText;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _includeDnsLeak = true;
    [ObservableProperty] private bool _includeChain = true;
    [ObservableProperty] private string? _browserCheckStatus;
    [ObservableProperty] private string _consistencyScore = "等待检测";
    [ObservableProperty] private string _consistencySummary = "先激活环境，再点击“开始检测”，自动采集浏览器环境并获取完整评分。";
    [ObservableProperty] private DiagStatus _consistencyStatus = DiagStatus.Unknown;

    [RelayCommand]
    private Task RunAsync() => IsBusy ? Task.CompletedTask : RunBusyAsync("正在检测…", async () =>
    {
        _report = null;
        HasReport = false;
        ConsistencyScore = "正在检测…";
        ConsistencySummary = "正在比较实际环境，未检测的项目不会算作正常。";
        ConsistencyStatus = DiagStatus.Unknown;
        BrowserCheckStatus = null;
        Groups.Clear();
        ErrorCount = WarningCount = OkCount = RepairableCount = 0;
        var browser = await CaptureBrowserAsync();
        var engine = new DiagnosticsEngine(Services.Settings, Services.Session);
        var progress = new Progress<DiagnosticItem>(item =>
        {
            BusyText = $"正在检测：{item.Title}";
            AddItem(item);
        });
        _report = await engine.RunAsync(new DiagnosticsOptions
        {
            IncludeChainTest = IncludeChain,
            IncludeDnsLeakTest = IncludeDnsLeak,
            BrowserCheck = browser,
        }, progress);

        // Re-render in the final, sorted order.
        Groups.Clear();
        ErrorCount = WarningCount = OkCount = RepairableCount = 0;
        foreach (var item in _report.Items) AddItem(item);
        LastRunText = $"{_report.StartedAt:HH:mm:ss} 完成，用时 {_report.Duration.TotalSeconds:F0} 秒";
        if (_report.Consistency is { } assessment)
        {
            ConsistencyScore = assessment.ScoreText;
            ConsistencySummary = assessment.Summary;
            ConsistencyStatus = assessment.RiskCount > 0 ? DiagStatus.Warning : assessment.PendingCount > 0 ? DiagStatus.Unknown : DiagStatus.Ok;
        }
        HasReport = true;
    });

    private void AddItem(DiagnosticItem item)
    {
        var name = DiagnosticItem.CategoryName(item.Category);
        var group = Groups.FirstOrDefault(g => g.Name == name);
        if (group == null)
        {
            group = new DiagnosticGroup(name, new ObservableCollection<DiagnosticItemViewModel>());
            Groups.Add(group);
        }
        group.Items.Add(new DiagnosticItemViewModel(item) { IsExpanded = item.Status is DiagStatus.Error or DiagStatus.Warning });
        switch (item.Status)
        {
            case DiagStatus.Error: ErrorCount++; break;
            case DiagStatus.Warning: WarningCount++; break;
            case DiagStatus.Ok: OkCount++; break;
        }
        if (item.Repair != null) RepairableCount++;
    }

    [RelayCommand]
    private async Task RepairAsync(DiagnosticItemViewModel? vm)
    {
        if (vm?.Repair is not { } repair) return;
        var ok = await Services.Dialogs.ConfirmChangesAsync(repair.Title, repair.Description,
            repair.Changes.Select(c => new ChangePreview(c, "", "")).ToList(),
            repair.RequiresElevation ? new[] { "此修复需要管理员授权，接下来会出现 Windows 用户账户控制提示。" } : null, "确认修复");
        if (!ok) return;
        await RunBusyAsync("正在修复…", async () =>
        {
            var message = await repair.ApplyAsync(CancellationToken.None);
            vm.RepairResult = message;
            Services.Dialogs.ShowSuccessToast(message);
        });
        await RunAsync();
    }

    private async Task<BrowserCheckResult?> CaptureBrowserAsync()
    {
        if (Services.Session.MixedPort is not { } port || Services.Session.Journal == null)
        {
            BrowserCheckStatus = "尚未激活环境，本次继续检测本机与线路配置。激活后再点“开始检测”，即可自动采集专属浏览器环境。";
            return null;
        }
        if (Services.Session.Status.State != SessionState.Protected || !Services.Session.Status.GateOpen)
        {
            BrowserCheckStatus = "当前环境未就绪或处于阻断状态，未打开浏览器检测页；请先在“概览”重新验证，再点“开始检测”。";
            return null;
        }
        BusyText = "正在自动采集专属浏览器环境…";
        try
        {
            var sessionId = Services.Session.Journal?.SessionId;
            await using var server = new BrowserCheckServer();
            var error = DedicatedBrowser.Launch(Services.Settings.Current.Browser, port, server.Url);
            if (error != null)
            {
                BrowserCheckStatus = "浏览器检测未完成：" + error + "；其余检测继续进行。";
                return null;
            }
            BrowserCheckStatus = "已在专属浏览器中打开检测页面，等待结果…";
            var result = await server.WaitAsync(TimeSpan.FromSeconds(90));
            if (result == null)
            {
                BrowserCheckStatus = "浏览器采样超时，其余检测继续进行。请确认浏览器能打开本机检测页，再点“开始检测”。";
                return null;
            }
            if (sessionId != Services.Session.Journal?.SessionId || port != Services.Session.MixedPort
                || Services.Session.Status.State != SessionState.Protected || !Services.Session.Status.GateOpen)
            {
                BrowserCheckStatus = "检测期间环境状态已变化，浏览器采样已丢弃，请在环境稳定后重新检测。";
                return null;
            }
            BrowserCheckStatus = $"已收到浏览器结果（{DateTime.Now:HH:mm:ss}），已加入下方报告。";
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("Diagnostics", "浏览器采样失败：" + ex.Message);
            BrowserCheckStatus = "浏览器检测未完成：" + ex.Message + "；其余检测继续进行。";
            return null;
        }
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (_report == null) return;
        System.Windows.Clipboard.SetText(_report.ToText());
        Services.Dialogs.ShowSuccessToast("报告已复制（已自动隐藏密码等敏感信息）");
    }

    [RelayCommand]
    private void ExportReport()
    {
        if (_report == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"CCNest-诊断报告-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "文本文件|*.txt",
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, _report.ToText());
            Services.Dialogs.ShowSuccessToast("报告已导出（不含密码）");
        }
    }

    [RelayCommand]
    private static void ToggleExpand(DiagnosticItemViewModel? vm)
    {
        if (vm != null) vm.IsExpanded = !vm.IsExpanded;
    }
}
