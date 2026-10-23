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
        _updateFeedUrl = s.Advanced.UpdateFeedUrl;
    }

    [ObservableProperty] private string _updateFeedUrl;
    [ObservableProperty] private string? _updateStatus;

    /// <summary>
    /// Checks a GitHub-releases style feed ({"tag_name": "v1.2.3", "html_url": "..."}). CCNest never downloads
    /// or runs installers itself: a newer version only opens its release page in the default browser.
    /// Installer upgrades keep all data in %LOCALAPPDATA%\CCNest.
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        var feed = UpdateFeedUrl.Trim();
        if (feed.Length == 0)
        {
            UpdateStatus = "未配置更新源。可填写发布页 API 地址，例如 https://api.github.com/repos/<所有者>/<仓库>/releases/latest";
            return;
        }
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            UpdateStatus = "更新源必须是 https:// 地址";
            return;
        }
        Services.Settings.Update(x => x.Advanced.UpdateFeedUrl = feed);
        UpdateStatus = "正在检查…";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CCNest-UpdateCheck");
            using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(uri));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var page = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !System.Version.TryParse(Version, out var current))
            {
                UpdateStatus = $"无法识别版本号：{tag}";
                return;
            }
            if (latest <= current)
            {
                UpdateStatus = $"已是最新版本（{Version}）";
                return;
            }
            UpdateStatus = $"发现新版本 {tag}";
            if (page != null && Uri.TryCreate(page, UriKind.Absolute, out var pageUri) && pageUri.Scheme == Uri.UriSchemeHttps
                && await Services.Dialogs.ConfirmAsync("发现新版本", $"CCNest {tag} 已发布（当前 {Version}）。\n\n将在浏览器中打开发布页，请下载安装包后运行；你的设置和专属浏览器资料会保留。", "打开发布页"))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pageUri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatus = "检查更新失败：" + ex.Message;
        }
    }

    public override string Key => "settings";
    public override string Title => "设置";
    public override string Subtitle => "界面、监控频率、端口与关于信息。";
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
