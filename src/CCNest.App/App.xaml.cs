using System.Windows;
using System.Windows.Threading;
using CCNest.App.Services;
using CCNest.App.ViewModels;
using CCNest.App.Views;
using CCNest.Core.Infrastructure;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CCNest.App;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private PowerMonitor? _power;
    private bool _exiting;

    public string[] StartupArgs { get; init; } = Array.Empty<string>();
    public AppServices Services { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("App", "未处理的异常", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("App", "未观察到的任务异常", ex.Exception);
            ex.SetObserved();
        };

        Services = new AppServices();
        ApplyTheme(Services.Settings.Current.Ui.Theme);

        var vm = new MainViewModel(Services);
        _window = new MainWindow(vm);
        _tray = new TrayIcon(this, Services);
        _power = new PowerMonitor(Services.Session);
        Services.Session.Notification += (title, message) => Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(title, message));

        _window.Show();
        await vm.InitializeAsync(StartupArgs);
    }

    public void ApplyTheme(string theme)
    {
        var appTheme = theme switch
        {
            "Dark" => ApplicationTheme.Dark,
            "Light" => ApplicationTheme.Light,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, updateAccent: false);
        ApplicationAccentColorManager.Apply((System.Windows.Media.Color)Resources["BrandColor"], appTheme, false, false);
    }

    public void HandleArguments(string[] args, bool fromSecondInstance)
    {
        ShowMainWindow();
        _ = _window!.ViewModel.HandleArgumentsAsync(args, fromSecondInstance);
    }

    public void ShowMainWindow()
    {
        if (_window == null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public bool IsExiting => _exiting;

    public bool IsMainWindowVisible => _window is { IsVisible: true } w && w.WindowState != WindowState.Minimized;

    public void ShowBalloon(string title, string message) => _tray?.ShowBalloon(title, message);

    /// <summary>Exits CCNest. With <paramref name="restore"/>, an active session is ended and all settings restored first.</summary>
    public async Task ExitAsync(bool restore)
    {
        if (_exiting) return;
        if (Services.Session.Journal != null)
        {
            if (!restore)
            {
                Log.Warn("App", "未恢复即退出：按崩溃策略处理");
            }
            else
            {
                var result = await Services.Session.DeactivateAsync();
                if (!result.Success && _window != null)
                {
                    var keep = await Services.Dialogs.ConfirmAsync("部分设置未能恢复",
                        string.Join("\n", result.Problems.Select(p => $"• {p.Change.Title}：{p.Message}")) +
                        "\n\n仍要退出吗？未恢复的项目会在下次启动 CCNest 时提示处理。", "仍然退出", "取消");
                    if (!keep) return;
                }
            }
        }
        _exiting = true;
        _power?.Dispose();
        _tray?.Dispose();
        await Services.Session.DisposeAsync();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is signing out / shutting down: restore quickly while we still can.
        Log.Info("App", $"Windows 会话结束（{e.ReasonSessionEnding}），恢复原始设置");
        if (Services.Session.Journal != null) Services.Session.EmergencyRestoreForShutdown();
        base.OnSessionEnding(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("App", "界面异常", e.Exception);
        e.Handled = true;
        try
        {
            Services?.Dialogs.ShowErrorToast("发生错误：" + e.Exception.Message);
        }
        catch { /* ignore */ }
    }
}
