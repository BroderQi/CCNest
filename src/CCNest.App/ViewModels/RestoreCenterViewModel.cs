using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using CCNest.Core.SystemEnv;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed record ChangeRow(ChangeRecord Change, string Source)
{
    public string Title => Change.Title;
    public string Describe => Change.Describe;
    public ChangeState State => Change.State;
    public string StateText => Change.State switch
    {
        ChangeState.Pending => "待确认",
        ChangeState.Applied => "生效中",
        ChangeState.Restored => "已恢复",
        ChangeState.RestoreFailed => "恢复失败",
        ChangeState.Conflict => "已被修改",
        ChangeState.RestoredPendingReboot => "已恢复（需注销）",
        _ => Change.State.ToString(),
    };
    public string? Error => Change.LastError;
    public string Timing => Change.RestoreTiming switch
    {
        EffectTiming.SignOut => "恢复需注销",
        EffectTiming.Restart => "恢复需重启",
        EffectTiming.NewProcesses => "对新程序生效",
        _ => "立即生效",
    };
}

public sealed partial class RestoreCenterViewModel : PageViewModel
{
    public RestoreCenterViewModel(AppServices services) : base(services) { }

    public override string Key => "restore";
    public override string Title => "恢复中心";
    public override string Subtitle => "CCNest 对系统做过的每一项修改及其原始值。可在此查看并恢复，包括异常退出后遗留的设置。";
    public override SymbolRegular Icon => SymbolRegular.ArrowUndo24;

    public ObservableCollection<ChangeRow> SessionChanges { get; } = new();
    public ObservableCollection<ChangeRow> StandaloneChanges { get; } = new();
    public ObservableCollection<string> History { get; } = new();

    [ObservableProperty] private string _sessionText = "";
    [ObservableProperty] private bool _hasOrphan;
    [ObservableProperty] private bool _hasConflicts;

    public override Task OnNavigatedToAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Refresh()
    {
        SessionChanges.Clear();
        StandaloneChanges.Clear();
        History.Clear();
        var live = Services.Session.Journal;
        var orphan = live == null ? JournalStore.FindOrphanedSession() : null;
        HasOrphan = orphan != null;
        var journal = live ?? orphan;
        if (journal != null)
        {
            SessionText = live != null
                ? $"当前会话 {journal.SessionId} · {journal.StartedAt:MM-dd HH:mm} 开始 · 结束会话时自动恢复以下更改"
                : $"上次会话 {journal.SessionId}（{journal.StartedAt:MM-dd HH:mm}）未正常结束，以下更改仍未恢复";
            foreach (var c in journal.Changes) SessionChanges.Add(new ChangeRow(c, "会话"));
        }
        else
        {
            SessionText = "当前没有活动会话。";
        }
        foreach (var c in JournalStore.LoadSystemEnv().Changes) StandaloneChanges.Add(new ChangeRow(c, "独立修改"));
        HasConflicts = SessionChanges.Concat(StandaloneChanges).Any(r => r.State is ChangeState.Conflict or ChangeState.RestoreFailed);

        var historyDir = Path.Combine(AppPaths.StateDir, "history");
        if (Directory.Exists(historyDir))
        {
            foreach (var f in Directory.GetFiles(historyDir, "session-*.json").OrderByDescending(f => f).Take(15))
            {
                var j = AtomicFile.ReadJson<SessionJournal>(f);
                if (j == null) continue;
                var restored = j.Changes.Count(c => c.State is ChangeState.Restored or ChangeState.RestoredPendingReboot);
                History.Add($"{j.StartedAt:yyyy-MM-dd HH:mm} · {j.Level} · {j.Changes.Count} 项更改，{restored} 项已恢复{(j.CleanShutdown ? "" : " · 非正常结束")}");
            }
        }
    }

    [RelayCommand]
    private async Task RestoreOrphanAsync()
    {
        var orphan = JournalStore.FindOrphanedSession();
        if (orphan == null) return;
        await RunBusyAsync("正在恢复…", async () =>
        {
            var outcomes = await RecoveryService.RestoreOrphanedAsync(orphan);
            var failed = outcomes.Count(o => !o.Success);
            if (failed == 0) Services.Dialogs.ShowSuccessToast("已全部恢复");
            else await Services.Dialogs.ShowMessageAsync("部分未恢复", string.Join("\n", outcomes.Where(o => !o.Success).Select(o => $"• {o.Change.Title}：{o.Message}")));
        });
        Refresh();
    }

    [RelayCommand]
    private async Task RestoreStandaloneAsync()
    {
        await RunBusyAsync("正在恢复…", async () =>
        {
            var outcomes = await SystemEnvService.RestoreStandaloneAsync();
            if (outcomes.Count == 0) Services.Dialogs.ShowToast("无需恢复", "没有待恢复的独立修改。");
            else await Services.Dialogs.ShowMessageAsync("恢复结果", string.Join("\n", outcomes.Select(o => $"{(o.Success ? "✓" : "!")} {o.Change.Title}：{o.Message}")));
        });
        Refresh();
    }

    [RelayCommand]
    private async Task ForceRestoreAsync()
    {
        var ok = await Services.Dialogs.ConfirmAsync("强制恢复",
            "部分设置在 CCNest 修改之后又被其他程序或你手动更改过。强制恢复会用 CCNest 保存的原始值覆盖这些新值。\n\n确定继续吗？",
            "强制恢复", "取消", ControlAppearance.Danger);
        if (!ok) return;
        await RunBusyAsync("正在强制恢复…", async () =>
        {
            var executor = new ChangeExecutor(null);
            var standalone = JournalStore.LoadSystemEnv();
            foreach (var c in standalone.Changes.Where(c => c.State == ChangeState.Conflict))
                await executor.RestoreAsync(c, force: true);
            JournalStore.SaveSystemEnv(standalone);
            var orphan = JournalStore.FindOrphanedSession();
            if (orphan != null) await RecoveryService.RestoreOrphanedAsync(orphan, force: true);
        });
        Refresh();
    }

    [RelayCommand]
    private void OpenStateFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.StateDir}\"") { UseShellExecute = true });
}
