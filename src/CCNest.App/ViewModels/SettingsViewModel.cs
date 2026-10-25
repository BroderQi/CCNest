using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core;
using CCNest.Core.Elevation;
using CCNest.Core.Infrastructure;
using CCNest.Core.Settings;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(AppServices services) : base(services)
    {
        var s = services.Settings.Current;
        _closeAction = s.Ui.CloseAction;
        _theme = s.Ui.Theme;
        _healthInterval = s.Monitor.HealthIntervalSeconds;
        _exitInterval = s.Monitor.ExitIpIntervalSeconds;
        _failureThreshold = s.Monitor.FailureThreshold;
        _mixedPort = s.Advanced.MixedPort;
        _mihomoPath = s.Advanced.MihomoPathOverride ?? "";
        _skipPreview = s.Ui.SkipActivationPreview;
        _autoCheckUpdates = s.Updates.AutoCheck;
    }

    /// <summary>Release discovery and one-click update from github.com/BroderQi/CCNest.</summary>
    public UpdateController Updates => Services.Updates;

    [ObservableProperty] private bool _autoCheckUpdates;

    partial void OnAutoCheckUpdatesChanged(bool value) => Services.Settings.Update(s => s.Updates.AutoCheck = value);

    public override string Key => "settings";
    public override string Title => "设置";
    public override string Subtitle => "界面、监控频率、端口、软件更新与关于信息。";
    public override SymbolRegular Icon => SymbolRegular.Settings24;

    public static IReadOnlyList<KeyValuePair<string, string>> Themes { get; } = new KeyValuePair<string, string>[]
    {
        new("System", "跟随系统"), new("Light", "浅色"), new("Dark", "深色"),
    };

    [ObservableProperty] private CloseAction _closeAction;
    [ObservableProperty] private string _theme;
    [ObservableProperty] private int _healthInterval;
    [ObservableProperty] private int _exitInterval;
    [ObservableProperty] private int _failureThreshold;
    [ObservableProperty] private int _mixedPort;
    [ObservableProperty] private string _mihomoPath;
    [ObservableProperty] private bool _skipPreview;

    public string Version => AppInfo.Version;
    public string DataDir => AppPaths.Root;
    public string CoreInfo => $"mihomo {MihomoBinary.PinnedVersion}（GPL-3.0，独立进程运行）";

    partial void OnThemeChanged(string value)
    {
        Services.Settings.Update(s => s.Ui.Theme = value);
        App.Current.ApplyTheme(value);
    }

    [RelayCommand]
    private void Save()
    {
        if (MixedPort is < 1024 or > 65535)
        {
            Services.Dialogs.ShowErrorToast("端口必须在 1024-65535 之间");
            return;
        }
        if (!string.IsNullOrWhiteSpace(MihomoPath) && !File.Exists(MihomoPath))
        {
            Services.Dialogs.ShowErrorToast("指定的 mihomo.exe 不存在");
            return;
        }
        Services.Settings.Update(s =>
        {
            s.Ui.CloseAction = CloseAction;
            s.Ui.SkipActivationPreview = SkipPreview;
            s.Monitor.HealthIntervalSeconds = Math.Clamp(HealthInterval, 5, 600);
            s.Monitor.ExitIpIntervalSeconds = Math.Clamp(ExitInterval, 30, 3600);
            s.Monitor.FailureThreshold = Math.Clamp(FailureThreshold, 1, 10);
            s.Advanced.MixedPort = MixedPort;
            s.Advanced.MihomoPathOverride = string.IsNullOrWhiteSpace(MihomoPath) ? null : MihomoPath.Trim();
        });
        Services.Dialogs.ShowSuccessToast(Services.Session.Journal != null ? "已保存，部分设置将在下次激活时生效" : "已保存");
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });

    [RelayCommand]
    private void OpenLicenses()
    {
        var path = Path.Combine(AppPaths.InstallDir, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        else Services.Dialogs.ShowToast("未找到", "THIRD-PARTY-NOTICES.md 不存在");
    }
}
