using CommunityToolkit.Mvvm.ComponentModel;
using CCNest.App.Services;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(AppServices services) => Services = services;

    protected AppServices Services { get; }

    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public abstract SymbolRegular Icon { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    /// <summary>Called every time the page becomes visible.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    protected static void Ui(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    protected async Task RunBusyAsync(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Core.Infrastructure.Log.Error(Key, text + " 失败", ex);
            Services.Dialogs.ShowErrorToast(ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }
}
