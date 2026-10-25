using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CCNest.App.Services;

public sealed record ChangePreview(string Title, string Detail, string Timing);

/// <summary>Fluent content dialogs and toast notifications hosted by the main window.</summary>
public sealed class DialogService
{
    private ContentDialogHost? _host;
    private int _openDialogs;

    /// <summary>True while a content dialog is shown (only one can be open at a time).</summary>
    public bool IsDialogOpen => _openDialogs > 0;
    private readonly SnackbarService _snackbar = new() { DefaultTimeOut = TimeSpan.FromSeconds(5) };

    public void Attach(ContentDialogHost host, SnackbarPresenter presenter)
    {
        _host = host;
        _snackbar.SetSnackbarPresenter(presenter);
    }

    public async Task<ContentDialogResult> ShowAsync(string title, object content, string? primary, string? secondary, string close,
        ControlAppearance primaryAppearance = ControlAppearance.Primary, double width = 520)
    {
        if (_host == null)
        {
            System.Windows.MessageBox.Show(content as string ?? title, title);
            return ContentDialogResult.None;
        }
        var dialog = new ContentDialog(_host)
        {
            Title = title,
            Content = content is string s ? Text(s) : content,
            PrimaryButtonText = primary ?? "",
            SecondaryButtonText = secondary ?? "",
            CloseButtonText = close,
            PrimaryButtonAppearance = primaryAppearance,
            DialogMaxWidth = width + 80,
            DefaultButton = primary != null ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };
        if (primary == null) dialog.IsPrimaryButtonEnabled = false;
        if (secondary == null) dialog.IsSecondaryButtonEnabled = false;
        _openDialogs++;
        try
        {
            return await dialog.ShowAsync(CancellationToken.None);
        }
        finally
        {
            _openDialogs--;
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirm = "确定", string cancel = "取消",
        ControlAppearance appearance = ControlAppearance.Primary) =>
        await ShowAsync(title, message, confirm, null, cancel, appearance) == ContentDialogResult.Primary;

    public Task ShowMessageAsync(string title, string message) => ShowAsync(title, message, null, null, "知道了");

    /// <summary>Shows exactly what will be changed (and when it takes effect) and asks for approval.</summary>
    public async Task<bool> ConfirmChangesAsync(string title, string intro, IReadOnlyList<ChangePreview> changes, IReadOnlyList<string>? notes = null,
        string confirm = "确认应用")
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(intro, 0, 0, 0, 12));
        foreach (var c in changes)
        {
            var border = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = c.Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 2, 0, 0) });
            if (!string.IsNullOrEmpty(c.Timing))
                sp.Children.Add(new TextBlock { Text = "生效方式：" + c.Timing, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("BrandBrush"), Margin = new Thickness(0, 2, 0, 0) });
            border.Child = sp;
            panel.Children.Add(border);
        }
        if (notes != null)
            foreach (var n in notes)
                panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = n, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(Text("所有原始值都会先保存，可在“恢复中心”一键撤销。", 0, 10, 0, 0, 0.7));
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return await ShowAsync(title, scroll, confirm, null, "取消", width: 560) == ContentDialogResult.Primary;
    }

    public void ShowToast(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
            _snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(5)));

    public void ShowSuccessToast(string message) => ShowToast("完成", message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
    public void ShowErrorToast(string message) => ShowToast("出错了", message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);

    private static TextBlock Text(string s, double l = 0, double t = 0, double r = 0, double b = 0, double opacity = 1) =>
        new() { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(l, t, r, b), Opacity = opacity, LineHeight = 20 };
}
