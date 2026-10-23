using System.Drawing;
using CCNest.Core.Session;
using Forms = System.Windows.Forms;

namespace CCNest.App.Services;

/// <summary>Notification-area icon: status at a glance and quick actions while the window is hidden.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly App _app;
    private readonly AppServices _services;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _toggle;

    public TrayIcon(App app, AppServices services)
    {
        _app = app;
        _services = services;
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "CCNest",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        _status = new Forms.ToolStripMenuItem("未激活") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开 CCNest", null, (_, _) => _app.ShowMainWindow());
        menu.Items.Add("打开 Claude 专属浏览器", null, (_, _) => _app.HandleArguments(new[] { "--open-browser" }, false));
        _toggle = new Forms.ToolStripMenuItem("激活 Claude 环境", null, (_, _) => _app.HandleArguments(new[] { "--activate" }, false));
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("结束会话并退出", null, async (_, _) => await _app.Dispatcher.InvokeAsync(() => _app.ExitAsync(restore: true)).Task.Unwrap());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _app.ShowMainWindow();
        services.Session.StatusChanged += s => _app.Dispatcher.BeginInvoke(() => Update(s));
    }

    private void Update(SessionStatusInfo s)
    {
        var text = s.State switch
        {
            SessionState.Protected => $"受保护 · 出口 {s.ExitIp}",
            SessionState.Blocked => "已阻断 · " + s.Headline,
            SessionState.Degraded => "线路中断 · 流量已安全阻断",
            SessionState.Activating => "正在激活…",
            SessionState.Deactivating => "正在恢复…",
            _ => "未激活",
        };
        _status.Text = text;
        _icon.Text = ("CCNest · " + text).Length > 63 ? ("CCNest · " + text)[..63] : "CCNest · " + text;
        _toggle.Text = s.State == SessionState.Inactive ? "激活 Claude 环境" : "显示状态";
    }

    public void ShowBalloon(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ccnest.ico"))?.Stream;
            if (stream != null) return new Icon(stream, new Size(16, 16));
        }
        catch { /* fall back */ }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
