using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Session;
using CCNest.Core.SystemEnv;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed record TimeZoneOption(string Id, string Display);
public sealed record GeoOption(int Id, string Name);

public sealed partial class SystemEnvViewModel : PageViewModel
{
    public SystemEnvViewModel(AppServices services) : base(services)
    {
        var s = services.Settings.Current.SystemEnv;
        _applyWithSession = s.ApplyWithSession;
        _regionalFormat = s.RegionalFormat;
        _regionalLocale = s.RegionalFormatLocale;
        _homeLocation = s.HomeLocation;
        _languageList = s.LanguageList;
        _preferredLanguage = s.PreferredLanguage;
        _displayLanguage = s.DisplayLanguage;
        _timeZone = s.TimeZone;
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones()) TimeZones.Add(new TimeZoneOption(tz.Id, tz.DisplayName));
        _selectedTimeZone = TimeZones.FirstOrDefault(t => t.Id == s.TimeZoneId) ?? TimeZones.FirstOrDefault(t => t.Id == "Pacific Standard Time");
        _selectedGeo = Geos.FirstOrDefault(g => g.Id == s.HomeGeoId) ?? Geos[0];
    }

    public override string Key => "system";
    public override string Title => "系统环境";
    public override string Subtitle => "按需切换 Windows 的英文工作环境（区域格式、语言、时区）。修改前保存原始值，可随时一键恢复。";
    public override SymbolRegular Icon => SymbolRegular.LocalLanguage24;

    public ObservableCollection<TimeZoneOption> TimeZones { get; } = new();
    public static IReadOnlyList<string> Locales { get; } = new[] { "en-US", "en-GB", "en-CA", "en-AU", "en-SG" };
    public static IReadOnlyList<string> Languages { get; } = new[] { "en-US", "en-GB" };
    public static IReadOnlyList<GeoOption> Geos { get; } = new[]
    {
        new GeoOption(244, "美国 United States"), new GeoOption(242, "英国 United Kingdom"), new GeoOption(39, "加拿大 Canada"),
        new GeoOption(12, "澳大利亚 Australia"), new GeoOption(215, "新加坡 Singapore"), new GeoOption(122, "日本 Japan"),
    };

    [ObservableProperty] private RegionalStatus? _current;
    [ObservableProperty] private bool _applyWithSession;
    [ObservableProperty] private bool _regionalFormat;
    [ObservableProperty] private string _regionalLocale;
    [ObservableProperty] private bool _homeLocation;
    [ObservableProperty] private GeoOption _selectedGeo;
    [ObservableProperty] private bool _languageList;
    [ObservableProperty] private string _preferredLanguage;
    [ObservableProperty] private bool _displayLanguage;
    [ObservableProperty] private bool _timeZone;
    [ObservableProperty] private TimeZoneOption? _selectedTimeZone;
    [ObservableProperty] private int _pendingRestoreCount;
    [ObservableProperty] private string? _currentLanguagesText;
    [ObservableProperty] private string? _uiLanguageText;

    public override Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunBusyAsync("正在读取 Windows 设置…", async () =>
    {
        Current = await RegionalSettings.ReadStatusAsync();
        CurrentLanguagesText = string.Join(" → ", Current.LanguageTags);
        UiLanguageText = Current.UiLanguageOverride != null ? $"{Current.UiLanguageOverride}（已设置覆盖）" : Current.CurrentUiLanguage;
        PendingRestoreCount = JournalStore.LoadSystemEnv().Changes.Count(c => c.NeedsRestore);
    });

    private void SaveSelection() => Services.Settings.Update(s =>
    {
        s.SystemEnv.ApplyWithSession = ApplyWithSession;
        s.SystemEnv.RegionalFormat = RegionalFormat;
        s.SystemEnv.RegionalFormatLocale = RegionalLocale;
        s.SystemEnv.HomeLocation = HomeLocation;
        s.SystemEnv.HomeGeoId = SelectedGeo.Id;
        s.SystemEnv.LanguageList = LanguageList;
        s.SystemEnv.PreferredLanguage = PreferredLanguage;
        s.SystemEnv.DisplayLanguage = DisplayLanguage;
        s.SystemEnv.TimeZone = TimeZone;
        if (SelectedTimeZone != null) s.SystemEnv.TimeZoneId = SelectedTimeZone.Id;
    });

    private static readonly HashSet<string> SelectionProperties = new()
    {
        nameof(ApplyWithSession), nameof(RegionalFormat), nameof(RegionalLocale), nameof(HomeLocation), nameof(SelectedGeo),
        nameof(LanguageList), nameof(PreferredLanguage), nameof(DisplayLanguage), nameof(TimeZone), nameof(SelectedTimeZone),
    };

    /// <summary>
    /// Every selection is saved immediately: with "随 Claude 环境自动应用" on, activation reads the saved
    /// selection, so an unsaved checkbox would otherwise silently apply the previous choice.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != null && SelectionProperties.Contains(e.PropertyName)) SaveSelection();
    }

    [RelayCommand]
    private Task ApplyNowAsync() => RunBusyAsync("正在准备更改…", async () =>
    {
        SaveSelection();
        if (Services.Session.Journal != null && ApplyWithSession)
        {
            Services.Dialogs.ShowToast("提示", "已选择“随会话应用”，当前会话期间不建议再单独应用。");
        }
        var (plan, notes) = await SystemEnvService.PlanAsync(Services.Settings.Current.SystemEnv);
        if (plan.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("无需更改", notes.Count > 0 ? string.Join("\n\n", notes) : "当前 Windows 设置已经符合你的选择。");
            return;
        }
        var previews = plan.Select(p => new ChangePreview(p.Change.Title, p.Summary, SystemEnvService.Describe(p.Timing))).ToList();
        if (!await Services.Dialogs.ConfirmChangesAsync("应用 Windows 环境设置", "将修改以下当前用户设置（不修改系统区域设置、字体或驱动）：", previews, notes)) return;

        BusyText = "正在应用…";
        var results = await SystemEnvService.ApplyStandaloneAsync(plan);
        var failed = results.Where(r => r.Error != null).ToList();
        var signOut = results.Any(r => r.Error == null && r.Change.Timing == EffectTiming.SignOut);
        if (failed.Count > 0)
            await Services.Dialogs.ShowMessageAsync("部分设置未能应用", string.Join("\n", failed.Select(f => $"• {f.Change.Change.Title}：{f.Error}")));
        else
            Services.Dialogs.ShowSuccessToast(signOut ? "已应用。显示语言需要注销后生效。" : "已应用。新打开的程序将使用新设置。");
        await RefreshAsync();
    });

    [RelayCommand]
    private Task RestoreAsync() => RunBusyAsync("正在恢复原始设置…", async () =>
    {
        var journal = JournalStore.LoadSystemEnv();
        var pending = journal.Changes.Where(c => c.NeedsRestore).ToList();
        if (pending.Count == 0)
        {
            Services.Dialogs.ShowToast("无需恢复", "没有由 CCNest 单独应用的系统设置。");
            return;
        }
        var previews = pending.Select(c => new ChangePreview(c.Title, "恢复为：" + c.Describe.Split('→')[0].Trim(), SystemEnvService.Describe(c.RestoreTiming))).ToList();
        if (!await Services.Dialogs.ConfirmChangesAsync("恢复原始 Windows 设置", "将恢复以下设置：", previews, null, "恢复")) return;
        var outcomes = await SystemEnvService.RestoreStandaloneAsync();
        var lines = outcomes.Select(o => $"{(o.Success ? "✓" : "!")} {o.Change.Title}：{o.Message}");
        await Services.Dialogs.ShowMessageAsync("恢复结果", string.Join("\n", lines));
        await RefreshAsync();
    });
}
