using CCNest.Core.Mihomo;
using CCNest.Core.Session;
using CCNest.Core.Settings;

namespace CCNest.App.Services;

/// <summary>Composition root shared by all view models.</summary>
public sealed class AppServices
{
    public AppServices()
    {
        Settings = new SettingsStore();
        Session = new SessionManager(Settings);
        Dialogs = new DialogService();
    }

    public SettingsStore Settings { get; }
    public SessionManager Session { get; }
    public DialogService Dialogs { get; }

    /// <summary>Most recent detection result (shared between pages).</summary>
    public ClashEnvironment? LastClash { get; set; }

    public event Action<string>? NavigationRequested;

    public void Navigate(string page) => NavigationRequested?.Invoke(page);

    public bool HasPendingRecovery() => JournalStore.FindOrphanedSession() != null;
}
