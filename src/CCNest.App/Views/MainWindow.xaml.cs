using System.ComponentModel;
using CCNest.App.ViewModels;
using CCNest.Core.Settings;
using Wpf.Ui.Controls;

namespace CCNest.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        App.Current.Services.Dialogs.Attach(DialogHost, SnackbarPresenter);
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: false);
    }

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Closing the window: with an active session the user chooses between keeping protection in the
    /// tray or ending the session (restoring the original Windows environment) and exiting.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (App.Current.IsExiting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        var services = App.Current.Services;
        var action = services.Settings.Current.Ui.CloseAction;
        var active = services.Session.Journal != null;

        if (action == CloseAction.MinimizeToTray)
        {
            Hide();
            return;
        }
        if (!active || action == CloseAction.ExitAndRestore)
        {
            await App.Current.ExitAsync(restore: true);
            return;
        }

        var result = await services.Dialogs.ShowAsync("关闭 CCNest",
            "Claude 专属环境正在运行。\n\n• 结束并退出：恢复激活前的 Windows 网络与系统设置，然后退出。\n• 最小化到托盘：保持保护，CCNest 在通知区域继续运行。",
            "结束并退出", "最小化到托盘", "取消");
        if (result == ContentDialogResult.Primary)
        {
            await App.Current.ExitAsync(restore: true);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Hide();
            services.Dialogs.ShowToast("CCNest 仍在运行", "保护持续生效，可从通知区域图标打开。");
        }
    }
}
