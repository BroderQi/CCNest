using System.Net.NetworkInformation;
using CCNest.Core.Session;
using Microsoft.Win32;

namespace CCNest.App.Services;

/// <summary>
/// Sleep/wake and network-change handling. Before sleep the gate is closed; after wake or an address
/// change the session re-verifies the exit IP before traffic flows again (when enabled).
/// </summary>
public sealed class PowerMonitor : IDisposable
{
    private readonly SessionManager _session;
    private readonly System.Threading.Timer _debounce;

    public PowerMonitor(SessionManager session)
    {
        _session = session;
        _debounce = new System.Threading.Timer(_ => _session.OnResumeOrNetworkChange("网络发生变化"), null, Timeout.Infinite, Timeout.Infinite);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _session.OnSuspend();
        else if (e.Mode == PowerModes.Resume) _session.OnResumeOrNetworkChange("系统从睡眠中唤醒", isResume: true);
    }

    // Address changes come in bursts (and CCNest's own TUN adapter causes some); debounce them.
    private void OnNetworkChanged(object? sender, EventArgs e) => _debounce.Change(TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _debounce.Dispose();
    }
}
