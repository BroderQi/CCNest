using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Infrastructure;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed partial class LogsViewModel : PageViewModel
{
    private const int MaxRows = 1500;

    public LogsViewModel(AppServices services) : base(services)
    {
        foreach (var e in Log.Snapshot().TakeLast(MaxRows)) Entries.Add(e);
        Log.EntryWritten += e => Ui(() =>
        {
            if (!Matches(e)) return;
            Entries.Add(e);
            while (Entries.Count > MaxRows) Entries.RemoveAt(0);
        });
    }

    public override string Key => "logs";
    public override string Title => "日志";
    public override string Subtitle => "运行日志与技术细节。密码、令牌等敏感信息在写入前已自动隐藏。";
    public override SymbolRegular Icon => SymbolRegular.DocumentText24;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    [ObservableProperty] private bool _showDebug;
    [ObservableProperty] private string _filter = "";

    partial void OnShowDebugChanged(bool value) => Reload();
    partial void OnFilterChanged(string value) => Reload();

    private bool Matches(LogEntry e) =>
        (ShowDebug || e.Level != LogLevel.Debug) &&
        (string.IsNullOrWhiteSpace(Filter) || e.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    private void Reload()
    {
        Entries.Clear();
        foreach (var e in Log.Snapshot().Where(Matches).TakeLast(MaxRows)) Entries.Add(e);
    }

    [RelayCommand]
    private void OpenFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDir}\"") { UseShellExecute = true });

    [RelayCommand]
    private void Copy()
    {
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Entries.Select(e => e.ToString())));
        Services.Dialogs.ShowSuccessToast("日志已复制");
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();
}
