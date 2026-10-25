using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.Core;
using CCNest.Core.Infrastructure;
using CCNest.Core.Update;
using Wpf.Ui.Controls;

namespace CCNest.App.Services;

/// <summary>
/// Discovers new CCNest releases on GitHub (BroderQi/CCNest) and installs them on request.
/// Automatic checks run shortly after start and then twice a day while CCNest is running; nothing
/// is installed without the user's confirmation, and an active session is always ended (original
/// settings restored) before CCNest exits for the update.
/// </summary>
public sealed partial class UpdateController : ObservableObject
{
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(12);
    private readonly AppServices _services;
    private bool _autoLoopStarted;

    public UpdateController(AppServices services) => _services = services;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    private UpdateInfo? _available;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWorking;

    public bool HasUpdate => Available != null;
    public string InstallButtonText => Available != null ? $"更新到 {Available.VersionText}" : "立即更新";
    public string CurrentVersion => AppInfo.Version;
    public string SourceDescription => $"更新源：github.com/{UpdateService.Repository} · " +
        (UpdateService.CurrentInstallKind == InstallKind.Installer ? "安装版，更新时需要管理员授权" : "便携版，更新时替换当前文件夹中的程序");

    /// <summary>Called once the main window is ready (after first-run and recovery dialogs).</summary>
    public void OnStartup(string[] args)
    {
        var settings = _services.Settings.Current.Updates;
        var current = AppInfo.Version;
        if (Program.Has(args, "--update-failed"))
        {
            _services.Dialogs.ShowToast("更新未完成", "可能取消了管理员授权或安装被中断，当前仍为 " + current + "。可在“设置 → 关于”中重试。",
                ControlAppearance.Caution, SymbolRegular.Warning24);
        }
        else if (!string.IsNullOrEmpty(settings.LastRunVersion) && settings.LastRunVersion != current
                 && UpdateService.TryParseVersion(settings.LastRunVersion, out var last) && UpdateService.CurrentVersion > last)
        {
            _services.Dialogs.ShowSuccessToast($"CCNest 已更新到 {current}");
        }
        if (settings.LastRunVersion != current) _services.Settings.Update(s => s.Updates.LastRunVersion = current);
        UpdateService.CleanupDownloads();
        StartAutoCheckLoop();
    }

    private async void StartAutoCheckLoop()
    {
        if (_autoLoopStarted) return;
        _autoLoopStarted = true;
        await Task.Delay(TimeSpan.FromSeconds(8));
        while (true)
        {
            try
            {
                var s = _services.Settings.Current.Updates;
                var due = s.LastCheckUtc == null || DateTime.UtcNow - s.LastCheckUtc.Value >= AutoCheckInterval;
                if (s.AutoCheck && due && !IsWorking) await CheckCoreAsync(userInitiated: false);
            }
            catch (Exception ex)
            {
                Log.Debug("Update", "自动检查更新失败：" + ex.Message);
            }
            await Task.Delay(TimeSpan.FromHours(1));
        }
    }

    [RelayCommand]
    private Task CheckAsync() => CheckCoreAsync(userInitiated: true);

    private async Task CheckCoreAsync(bool userInitiated)
    {
        if (IsWorking) return;
        IsWorking = true;
        if (userInitiated) Status = "正在检查更新…";
        try
        {
            var feed = UpdateService.ResolveFeed(_services.Settings.Current.Advanced.UpdateFeedUrl);
            var latest = await UpdateService.FetchLatestAsync(feed);
            _services.Settings.Update(s => s.Updates.LastCheckUtc = DateTime.UtcNow);
            if (latest.Version <= UpdateService.CurrentVersion)
            {
                Available = null;
                Status = $"已是最新版本（{AppInfo.Version}）";
                return;
            }
            Available = latest;
            Status = $"发现新版本 {latest.VersionText}（当前 {AppInfo.Version}）";
            Log.Info("Update", $"发现新版本 {latest.Tag}");
            if (!userInitiated && _services.Settings.Current.Updates.SkippedVersion == latest.VersionText) return;
            IsWorking = false;
            await PromptAsync(latest, userInitiated);
        }
        catch (Exception ex)
        {
            Log.Warn("Update", "检查更新失败：" + ex.Message);
            if (userInitiated) Status = "检查更新失败：" + FriendlyError(ex);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private async Task PromptAsync(UpdateInfo info, bool userInitiated)
    {
        if (!userInitiated && (!App.Current.IsMainWindowVisible || _services.Dialogs.IsDialogOpen))
        {
            // Never pop a dialog over another one or onto a hidden window: just let the user know.
            App.Current.ShowBalloon("CCNest 有新版本", $"{info.VersionText} 已发布，可在“设置 → 关于”中一键更新。");
            _services.Dialogs.ShowToast("发现新版本", $"CCNest {info.VersionText} 已发布，可在“设置 → 关于”中一键更新。",
                ControlAppearance.Info, SymbolRegular.ArrowDownload24);
            return;
        }

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(Text($"CCNest {info.VersionText} 已发布（当前 {AppInfo.Version}）" +
                                (info.PublishedAt is { } at ? $"，发布于 {at.ToLocalTime():yyyy-MM-dd}" : "") + "。"));
        var notes = CleanNotes(info.Notes);
        if (notes.Length > 0)
        {
            panel.Children.Add(new System.Windows.Controls.ScrollViewer
            {
                MaxHeight = 220,
                Margin = new System.Windows.Thickness(0, 10, 0, 0),
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = Text(notes, 0.8),
            });
        }
        panel.Children.Add(Text(UpdateService.CurrentInstallKind == InstallKind.Installer
            ? "更新会下载并校验安装包，然后退出 CCNest、静默安装新版本（需要管理员授权）并自动重新打开。"
            : "更新会下载并校验便携版，然后退出 CCNest、替换当前文件夹中的程序并自动重新打开。", 1, 12));
        if (_services.Session.Journal != null)
            panel.Children.Add(Text("Claude 环境正在运行：更新前会先结束会话并恢复原始设置，更新完成后请重新激活。", 1, 8));
        panel.Children.Add(Text("设置、加密凭据和专属浏览器资料都会保留。", 0.7, 8));

        var result = await _services.Dialogs.ShowAsync("发现新版本", panel, "立即更新", userInitiated ? null : "跳过此版本", "稍后", width: 560);
        if (result == ContentDialogResult.Primary) await InstallAsync();
        else if (result == ContentDialogResult.Secondary)
        {
            _services.Settings.Update(s => s.Updates.SkippedVersion = info.VersionText);
            Status = $"已跳过 {info.VersionText}，有更新的版本时会再次提醒；也可随时手动更新。";
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var info = Available;
        if (info == null || IsWorking) return;
        var kind = UpdateService.CurrentInstallKind;
        if (info.AssetFor(kind) == null || info.Checksums == null)
        {
            await OfferReleasePageAsync(info, "该版本的发布页缺少可自动安装的文件或校验文件。");
            return;
        }
        if (kind == InstallKind.Portable && !UpdateService.CanUpdatePortableInPlace())
        {
            await OfferReleasePageAsync(info, $"当前文件夹没有写入权限（{AppPaths.InstallDir}），无法自动替换便携版。");
            return;
        }

        IsWorking = true;
        try
        {
            // 1. Download and verify everything before touching the running session.
            IsDownloading = true;
            Progress = 0;
            Status = $"正在下载 {info.VersionText}…";
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Status = $"正在下载 {info.VersionText}… {p:P0}";
            });
            var package = await UpdateService.DownloadAsync(info, kind, progress);
            IsDownloading = false;
            var source = kind == InstallKind.Portable ? await Task.Run(() => UpdateService.ExtractPortable(package)) : package;
            Status = "下载完成，SHA-256 校验通过";

            // 2. End an active session first, so no proxy / WFP / system change is left behind.
            if (_services.Session.Journal != null)
            {
                Status = "正在结束会话并恢复原始设置…";
                var restore = await _services.Session.DeactivateAsync();
                if (!restore.Success)
                {
                    Status = "部分设置未能恢复，已取消更新";
                    await _services.Dialogs.ShowMessageAsync("已取消更新",
                        "以下设置未能恢复，为安全起见本次不更新：\n\n" +
                        string.Join("\n", restore.Problems.Select(p => $"• {p.Change.Title}：{p.Message}")) +
                        "\n\n请在“恢复中心”处理后再更新。");
                    return;
                }
            }

            // 3. Hand over to the apply script and exit; it restarts CCNest when done.
            Status = "正在启动更新程序…";
            UpdateService.StartApply(kind, source);
            await App.Current.ExitAsync(restore: true);
        }
        catch (Exception ex)
        {
            Log.Error("Update", "更新失败", ex);
            Status = "更新失败：" + FriendlyError(ex);
            _services.Dialogs.ShowErrorToast("更新失败：" + FriendlyError(ex));
        }
        finally
        {
            IsDownloading = false;
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = Available?.PageUrl.ToString() ?? UpdateService.ReleasesPageUrl;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task OfferReleasePageAsync(UpdateInfo info, string reason)
    {
        Status = reason;
        if (await _services.Dialogs.ConfirmAsync("无法自动更新", reason + "\n\n可以打开发布页手动下载 " + info.VersionText + "。", "打开发布页"))
            OpenReleasePage();
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        OperationCanceledException => "连接 GitHub 超时，请检查网络或代理后重试",
        System.Net.Http.HttpRequestException h => "无法连接 GitHub（" + h.Message + "）",
        _ => ex.Message,
    };

    /// <summary>Keeps the release notes readable in a plain text block (drops markdown markers and the build footer).</summary>
    private static string CleanNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !l.StartsWith("构建提交", StringComparison.Ordinal) && !l.StartsWith("**Full Changelog**", StringComparison.Ordinal))
            .Select(l => l.TrimStart('#', ' ').Replace("**", ""));
        var text = string.Join("\n", lines).Trim();
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        return text.Length > 1500 ? text[..1500] + "…" : text;
    }

    private static System.Windows.Controls.TextBlock Text(string s, double opacity = 1, double top = 0) => new()
    {
        Text = s,
        TextWrapping = System.Windows.TextWrapping.Wrap,
        LineHeight = 20,
        Opacity = opacity,
        Margin = new System.Windows.Thickness(0, top, 0, 0),
    };
}
