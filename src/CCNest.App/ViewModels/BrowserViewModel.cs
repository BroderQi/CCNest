using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Browser;
using CCNest.Core.Settings;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed record BrowserChoice(BrowserKind Kind, string Name, string? Path);

public sealed partial class BrowserViewModel : PageViewModel
{
    public BrowserViewModel(AppServices services) : base(services)
    {
        var s = services.Settings.Current.Browser;
        _acceptLanguages = s.AcceptLanguages;
        _restrictWebRtc = s.RestrictWebRtcToProxy;
        _uiLanguage = s.UiLanguage;
    }

    public override string Key => "browser";
    public override string Title => "专属浏览器";
    public override string Subtitle => "永久保留的 Claude 专属浏览环境：与日常浏览器完全隔离，登录状态与扩展长期保存。";
    public override SymbolRegular Icon => SymbolRegular.Window24;

    public ObservableCollection<BrowserChoice> Choices { get; } = new();

    [ObservableProperty] private BrowserChoice? _selectedChoice;
    [ObservableProperty] private DedicatedProfileInfo? _info;
    [ObservableProperty] private string _profileStatus = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _acceptLanguages;
    [ObservableProperty] private string _uiLanguage;
    [ObservableProperty] private bool _restrictWebRtc;

    public static IReadOnlyList<string> LanguagePresets { get; } = new[] { "en-US,en", "en-GB,en", "en-US,en,zh-CN,zh", "zh-CN,zh,en-US,en" };

    public override Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var current = Services.Settings.Current.Browser;
        Choices.Clear();
        Choices.Add(new BrowserChoice(BrowserKind.Auto, "自动选择（优先 Chrome）", null));
        foreach (var b in BrowserLocator.FindAll()) Choices.Add(new BrowserChoice(b.Kind, $"{b.Name} {b.Version}", b.Path));
        SelectedChoice = Choices.FirstOrDefault(c => c.Kind == current.Preferred) ?? Choices[0];
        Info = await Task.Run(() => DedicatedBrowser.Inspect(Services.Settings.Current.Browser));
        ProfileStatus = Info.Browser == null ? "未找到可用的 Chromium 内核浏览器"
            : Info.Exists ? $"{Info.Browser.Name} · 专属资料已建立{(Info.CreatedAt is { } c ? $"（{c:yyyy-MM-dd} 创建）" : "")}{(Info.Running ? " · 正在运行" : "")}"
            : $"{Info.Browser.Name} · 首次打开时创建专属资料";
        SizeText = Info.Exists ? $"{Info.SizeBytes / 1024.0 / 1024.0:F1} MB" : "—";
    }

    partial void OnSelectedChoiceChanged(BrowserChoice? value)
    {
        if (value == null) return;
        if (Services.Settings.Current.Browser.Preferred != value.Kind)
        {
            Services.Settings.Update(s => s.Browser.Preferred = value.Kind);
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    private Task OpenAsync() => ((MainViewModel)System.Windows.Application.Current.MainWindow!.DataContext).OpenBrowserAsync();

    [RelayCommand]
    private void SaveLanguage()
    {
        Services.Settings.Update(s =>
        {
            s.Browser.AcceptLanguages = AcceptLanguages.Trim();
            s.Browser.UiLanguage = UiLanguage.Trim();
            s.Browser.RestrictWebRtcToProxy = RestrictWebRtc;
        });
        var browser = BrowserLocator.Resolve(Services.Settings.Current.Browser);
        if (browser != null)
        {
            var error = DedicatedBrowser.ApplyLanguagePreference(browser, Services.Settings.Current.Browser);
            if (error != null)
            {
                Services.Dialogs.ShowErrorToast(error);
                return;
            }
        }
        Services.Dialogs.ShowSuccessToast("已保存，下次打开专属浏览器时生效");
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Info != null) DedicatedBrowser.OpenFolder(Info.UserDataDir);
    }

    [RelayCommand]
    private void CreateShortcut()
    {
        var error = DedicatedBrowser.CreateDesktopShortcut(Info?.Browser);
        if (error != null) Services.Dialogs.ShowErrorToast(error);
        else Services.Dialogs.ShowSuccessToast("已在桌面创建“Claude专属浏览器 (CCNest)”快捷方式");
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (Info?.Browser is not { } browser || !Info.Exists) return;
        var ok = await Services.Dialogs.ConfirmAsync("重置专属浏览器资料",
            "重置后，专属浏览器中的登录状态、Cookie、扩展和设置都会从当前资料中移除，下次打开时将是全新的资料。\n\n" +
            "为安全起见，CCNest 不会删除旧资料，而是将其移动到 browser 目录下的 archived-… 文件夹中，你可以手动恢复或删除。\n\n确定要重置吗？",
            "移走并重置", "取消", ControlAppearance.Danger);
        if (!ok) return;
        try
        {
            var archive = DedicatedBrowser.ResetProfile(browser);
            Services.Dialogs.ShowSuccessToast($"旧资料已保存在 {archive}");
        }
        catch (Exception ex)
        {
            Services.Dialogs.ShowErrorToast(ex.Message);
        }
        await RefreshAsync();
    }
}
