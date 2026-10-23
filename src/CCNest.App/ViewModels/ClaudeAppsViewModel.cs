using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using CCNest.Core.Claude;
using CCNest.Core.Infrastructure;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed partial class ClaudeAppsViewModel : PageViewModel
{
    public ClaudeAppsViewModel(AppServices services) : base(services)
    {
        var s = services.Settings.Current;
        _configureClaudeCode = s.Integrations.ConfigureClaudeCode;
        _setSystemProxy = s.Integrations.SetSystemProxy;
        _setUserEnv = s.Integrations.SetUserEnvironmentProxy;
    }

    public override string Key => "claude";
    public override string Title => "Claude 应用";
    public override string Subtitle => "Claude Code 与 Claude Desktop 的检测、代理接入方式与已知限制。";
    public override SymbolRegular Icon => SymbolRegular.Bot24;

    [ObservableProperty] private ClaudeCodeInfo? _code;
    [ObservableProperty] private ClaudeDesktopInfo? _desktop;
    [ObservableProperty] private string _codeStatus = "检测中…";
    [ObservableProperty] private string _desktopStatus = "检测中…";
    [ObservableProperty] private string? _codeProxyText;
    [ObservableProperty] private bool _configureClaudeCode;
    [ObservableProperty] private bool _setSystemProxy;
    [ObservableProperty] private bool _setUserEnv;

    public bool SessionActive => Services.Session.Journal != null;

    partial void OnConfigureClaudeCodeChanged(bool value) => Services.Settings.Update(s => s.Integrations.ConfigureClaudeCode = value);
    partial void OnSetSystemProxyChanged(bool value) => Services.Settings.Update(s => s.Integrations.SetSystemProxy = value);
    partial void OnSetUserEnvChanged(bool value) => Services.Settings.Update(s => s.Integrations.SetUserEnvironmentProxy = value);

    public override Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunBusyAsync("正在检测 Claude 应用…", async () =>
    {
        OnPropertyChanged(nameof(SessionActive));
        Code = await ClaudeCodeManager.DetectAsync();
        CodeStatus = Code.Installed
            ? $"已安装 · {Code.Version ?? "版本未知"} · {Code.InstallKind}" + (Code.RunningPids.Count > 0 ? $" · {Code.RunningPids.Count} 个进程运行中" : "")
            : "未检测到 Claude Code";
        CodeProxyText = Code.SettingsProxyEnv.Count == 0
            ? "settings.json 中未设置代理"
            : string.Join("\n", Code.SettingsProxyEnv.Select(kv => $"{kv.Key} = {kv.Value}"));
        Desktop = await ClaudeDesktopManager.DetectAsync();
        DesktopStatus = Desktop.Installed
            ? $"已安装 · {Desktop.Kind} {Desktop.Version}" + (Desktop.RunningPids.Count > 0 ? " · 正在运行" : "")
            : "未检测到 Claude Desktop";
    });

    [RelayCommand]
    private void OpenTerminal()
    {
        if (Services.Session.MixedPort is not { } port || Services.Session.Journal == null)
        {
            Services.Dialogs.ShowToast("需要先激活", "激活 Claude 环境后才能打开 Claude Code 专属终端。");
            return;
        }
        ClaudeCodeManager.LaunchTerminal(port);
    }

    [RelayCommand]
    private async Task LaunchDesktopAsync()
    {
        if (Services.Session.MixedPort is not { } port || Services.Session.Journal == null)
        {
            Services.Dialogs.ShowToast("需要先激活", "激活 Claude 环境后才能通过 CCNest 启动 Claude Desktop。");
            return;
        }
        Desktop ??= await ClaudeDesktopManager.DetectAsync();
        var error = ClaudeDesktopManager.LaunchWithProxy(Desktop, port);
        if (error != null) await Services.Dialogs.ShowMessageAsync("Claude Desktop", error);
        else Services.Dialogs.ShowSuccessToast("已通过 CCNest 启动 Claude Desktop");
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        var dir = Path.GetDirectoryName(ClaudeCodeManager.SettingsPath)!;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyEnvCommand()
    {
        var port = Services.Session.MixedPort ?? Services.Settings.Current.Advanced.MixedPort;
        var text = $"$env:HTTPS_PROXY=\"http://127.0.0.1:{port}\"; $env:HTTP_PROXY=\"http://127.0.0.1:{port}\"; $env:NO_PROXY=\"{ClaudeCodeManager.DefaultNoProxy}\"; claude";
        System.Windows.Clipboard.SetText(text);
        Services.Dialogs.ShowSuccessToast("已复制 PowerShell 命令");
        Log.Info("Claude", "已复制 Claude Code 代理命令");
    }
}
