using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Browser;
using CCNest.Core.Diagnostics;
using CCNest.Core.Infrastructure;
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
        DiagStatus.Warning => "注意",
        DiagStatus.Error => "需处理",
        _ => "无法确定",
    };

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _repairResult;
}

public sealed record DiagnosticGroup(string Name, ObservableCollection<DiagnosticItemViewModel> Items);

public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private BrowserCheckResult? _browserCheck;
    private DiagnosticReport? _report;

    public DiagnosticsViewModel(AppServices services) : base(services) { }

    public override string Key => "diagnostics";
    public override string Title => "环境检测";
    public override string Subtitle => "检查网络出口、代理线路、DNS、IPv6、路由、Claude 应用与 Windows 环境，并修复可自动处理的问题。";
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

    [RelayCommand]
    private Task RunAsync() => RunBusyAsync("正在检测…", async () =>
    {
        Groups.Clear();
        ErrorCount = WarningCount = OkCount = RepairableCount = 0;
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
            BrowserCheck = _browserCheck,
        }, progress);

        // Re-render in the final, sorted order.
        Groups.Clear();
        ErrorCount = WarningCount = OkCount = RepairableCount = 0;
        foreach (var item in _report.Items) AddItem(item);
        LastRunText = $"{_report.StartedAt:HH:mm:ss} 完成，用时 {_report.Duration.TotalSeconds:F0} 秒";
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

    [RelayCommand]
    private async Task BrowserCheckAsync()
    {
        if (Services.Session.MixedPort is not { } port || Services.Session.Journal == null)
        {
            await Services.Dialogs.ShowMessageAsync("需要先激活环境", "浏览器环境检测会在专属浏览器中打开一个本机页面，需要先激活 Claude 环境。");
            return;
        }
        await RunBusyAsync("正在专属浏览器中检测…", async () =>
        {
            await using var server = new BrowserCheckServer();
            var error = DedicatedBrowser.Launch(Services.Settings.Current.Browser, port, server.Url);
            if (error != null)
            {
                Services.Dialogs.ShowErrorToast(error);
                return;
            }
            BrowserCheckStatus = "已在专属浏览器中打开检测页面，等待结果…";
            var result = await server.WaitAsync(TimeSpan.FromSeconds(90));
            if (result == null)
            {
                BrowserCheckStatus = "等待超时：未收到检测页面的结果。";
                return;
            }
            _browserCheck = result;
            BrowserCheckStatus = $"已收到浏览器结果（{DateTime.Now:HH:mm:ss}），已加入下方报告。";
        });
        await RunAsync();
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
