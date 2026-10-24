using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core;
using CCNest.Core.Browser;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using CCNest.Core.Settings;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Dashboard = new DashboardViewModel(services, this);
        Pages = new ObservableCollection<PageViewModel>
        {
            Dashboard,
            new NetworkViewModel(services),
            new DiagnosticsViewModel(services),
            new ClaudeAppsViewModel(services),
            new BrowserViewModel(services),
            new SystemEnvViewModel(services),
            new RestoreCenterViewModel(services),
            new LogsViewModel(services),
        };
        FooterPages = new ObservableCollection<PageViewModel> { new GuideViewModel(services), new SettingsViewModel(services) };
        _selectedPage = Dashboard;
        _status = services.Session.Status;
        services.Session.StatusChanged += s => Ui(() => Status = s);
        services.NavigationRequested += key => Ui(() => Navigate(key));
    }

    public DashboardViewModel Dashboard { get; }
    public string WindowTitle => $"{AppInfo.DisplayName} · v{AppInfo.Version}";
    public ObservableCollection<PageViewModel> Pages { get; }
    public ObservableCollection<PageViewModel> FooterPages { get; }

    [ObservableProperty]
    private PageViewModel? _selectedPage;

    [ObservableProperty]
    private PageViewModel? _selectedFooterPage;

    [ObservableProperty]
    private SessionStatusInfo _status;

    public PageViewModel? CurrentPage => SelectedFooterPage ?? SelectedPage;

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        if (value != null) SelectedFooterPage = null;
        OnPropertyChanged(nameof(CurrentPage));
        if (value != null) _ = value.OnNavigatedToAsync();
    }

    partial void OnSelectedFooterPageChanged(PageViewModel? value)
    {
        if (value != null) SelectedPage = null;
        OnPropertyChanged(nameof(CurrentPage));
        if (value != null) _ = value.OnNavigatedToAsync();
    }

    public void Navigate(string key)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key);
        if (page != null) SelectedPage = page;
        else if (FooterPages.FirstOrDefault(p => p.Key == key) is { } footer) SelectedFooterPage = footer;
    }

    [RelayCommand]
    private void OpenGuide() => Navigate("guide");

    public async Task InitializeAsync(string[] args)
    {
        var settings = _services.Settings.Current;
        if (!settings.Ui.AcceptedNotice)
        {
            await ShowFirstRunNoticeAsync();
        }

        var orphan = JournalStore.FindOrphanedSession();
        if (orphan != null)
        {
            await ShowRecoveryAsync(orphan, launchedForRecovery: Program.Has(args, "--recover"));
        }
        else if (Program.Has(args, "--recover"))
        {
            // Watchdog already restored (AutoRestore policy) or nothing left: show a short report.
            var last = Directory.Exists(Path.Combine(AppPaths.StateDir, "history"))
                ? Directory.GetFiles(Path.Combine(AppPaths.StateDir, "history"), "session-*.json").OrderByDescending(f => f).FirstOrDefault()
                : null;
            _services.Dialogs.ShowToast("CCNest 已重新启动", last != null ? "上次会话已按“可用性优先”策略自动恢复原始设置。" : "未发现需要恢复的设置。");
        }

        if (!settings.Ui.FirstRunCompleted)
        {
            _services.Settings.Update(s => s.Ui.FirstRunCompleted = true);
            Navigate("network");
        }

        await HandleArgumentsAsync(args, false);
        await Dashboard.OnNavigatedToAsync();
    }

    public async Task HandleArgumentsAsync(string[] args, bool fromSecondInstance)
    {
        if (Program.Has(args, "--open-browser")) await OpenBrowserAsync();
        if (Program.Has(args, "--activate"))
        {
            Navigate("dashboard");
            if (_services.Session.Journal == null) await Dashboard.ActivateCommand.ExecuteAsync(null);
        }
        if (fromSecondInstance && Program.Has(args, "--recover") && JournalStore.FindOrphanedSession() is { } orphan)
            await ShowRecoveryAsync(orphan, true);
    }

    [RelayCommand]
    public async Task OpenBrowserAsync()
    {
        var port = _services.Session.MixedPort;
        if (port == null || _services.Session.Status.State == SessionState.Inactive)
        {
            var activate = await _services.Dialogs.ConfirmAsync("需要先激活 Claude 环境",
                "专属浏览器只通过 CCNest 的专属线路联网。环境未激活时它无法访问任何网站——这是为了防止你在错误的网络环境下登录 Claude。\n\n现在激活环境并打开专属浏览器吗？",
                "激活并打开");
            if (!activate) return;
            Navigate("dashboard");
            await Dashboard.ActivateCommand.ExecuteAsync(null);
            port = _services.Session.MixedPort;
            if (port == null || _services.Session.Journal == null) return;
        }
        var error = DedicatedBrowser.Launch(_services.Settings.Current.Browser, port.Value);
        if (error != null) _services.Dialogs.ShowErrorToast(error);
    }

    private async Task ShowFirstRunNoticeAsync()
    {
        var text = new System.Windows.Controls.TextBlock
        {
            TextWrapping = System.Windows.TextWrapping.Wrap,
            LineHeight = 21,
            Text =
                "CCNest 帮你为 Claude 建立一个稳定、可验证、可恢复的专属网络与浏览环境：\n\n" +
                "• 在现有 Clash / Mihomo 线路之后串联你自己购买的静态代理，并持续验证出口 IP；\n" +
                "• 线路异常时安全阻断，绝不悄悄回退到其他出口；\n" +
                "• 所有对系统的修改都会先记录原始值，结束时一键恢复。\n\n" +
                "使用须知：请仅在 Claude 服务支持的国家和地区、并遵守 Anthropic 使用条款的前提下使用。CCNest 不用于、也不会帮助绕过任何账号限制、地区资格要求或平台风控。CCNest 不提供代理服务，不收集任何数据，所有配置仅保存在本机。",
        };
        var result = await _services.Dialogs.ShowAsync("欢迎使用 CCNest", text, "我已了解", null, "退出", width: 560);
        if (result == ContentDialogResult.Primary) _services.Settings.Update(s => s.Ui.AcceptedNotice = true);
        else await App.Current.ExitAsync(false);
    }

    private async Task ShowRecoveryAsync(SessionJournal orphan, bool launchedForRecovery)
    {
        var pending = orphan.Changes.Where(c => c.NeedsRestore).ToList();
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            TextWrapping = System.Windows.TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
            Text = $"CCNest 上次（{orphan.StartedAt:MM-dd HH:mm} 开始的会话）没有正常结束。" +
                   (orphan.CrashPolicy == CrashPolicy.KeepBlocked
                       ? "按照“安全优先”策略，以下设置仍保持在激活状态，指向 CCNest 的应用目前无法联网（不会泄漏到其他出口）："
                       : "以下设置尚未恢复："),
        });
        foreach (var c in pending)
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"• {c.Title}：{c.Describe}",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(8, 2, 0, 2),
            });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new System.Windows.Thickness(0, 12, 0, 0),
            Text = "“恢复原始设置”会撤销上述全部更改（网络阻断规则需要管理员授权）。“重新激活”会先恢复，再重新建立受保护的专属环境。",
        });

        var result = await _services.Dialogs.ShowAsync("检测到未正常结束的会话", panel, "恢复原始设置", "重新激活", "稍后处理", width: 560);
        if (result == ContentDialogResult.None) return;

        var outcomes = await RecoveryService.RestoreOrphanedAsync(orphan);
        var failed = outcomes.Where(o => !o.Success).ToList();
        if (failed.Count > 0)
            await _services.Dialogs.ShowMessageAsync("部分设置未恢复", string.Join("\n", failed.Select(f => $"• {f.Change.Title}：{f.Message}")));
        else
            _services.Dialogs.ShowSuccessToast("上次会话的更改已全部恢复");

        if (result == ContentDialogResult.Secondary && failed.Count == 0)
        {
            Navigate("dashboard");
            await Dashboard.ActivateCommand.ExecuteAsync(null);
        }
    }

    private static void Ui(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
