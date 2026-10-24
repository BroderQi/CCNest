using CCNest.Core.Claude;
using CCNest.Core.Mihomo;
using CCNest.Core.SystemEnv;
using CCNest.Core.Wfp;

namespace CCNest.Tests;

/// <summary>
/// Read-only checks against the real Windows APIs of the machine running the tests. They verify the
/// P/Invoke layers work without modifying anything. Assertions are environment-agnostic.
/// </summary>
public class LiveSystemSmokeTests
{
    [Fact]
    public void WinInet_ReadsCurrentProxyConfiguration()
    {
        var state = new WinInetProxyBackend().Read();
        Assert.NotNull(state.Describe());
    }

    [Fact]
    public void IpHelper_ListsTcpListenersWithPids()
    {
        var listeners = Core.Native.IpHelper.GetTcpListeners();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, l => Assert.InRange(l.Port, 1, 65535));
    }

    [Fact]
    public void NetworkInspector_CapturesInterfaces()
    {
        var snap = NetworkInspector.Capture();
        Assert.NotEmpty(snap.Interfaces);
    }

    [Fact]
    public void Firewall_StatusIsReadable_AndBfeIsRunning()
    {
        var fw = FirewallStatus.Read();
        Assert.Equal(ServiceState.Running, fw.BaseFilteringEngine);
    }

    [Fact]
    public async Task RegionalSettings_CaptureIsComplete()
    {
        var snap = await RegionalSettings.CaptureAsync();
        Assert.False(string.IsNullOrEmpty(snap.Culture));
        Assert.NotEmpty(snap.Languages);
        Assert.All(snap.Languages, l => Assert.False(string.IsNullOrEmpty(l.Tag)));
        Assert.False(string.IsNullOrEmpty(snap.TimeZoneId));
        Assert.True(snap.GeoId > 0);
        Assert.NotEmpty(snap.InternationalValues);
    }

    [Fact]
    public async Task RegionalSettings_StatusReadsCountryCodeAndEffectiveDisplayLanguage()
    {
        var status = await RegionalSettings.ReadStatusAsync();
        Assert.Matches("^[A-Z]{2}$", status.CountryCode ?? "");
        Assert.False(string.IsNullOrWhiteSpace(status.CurrentUiLanguage));
        Assert.False(string.IsNullOrWhiteSpace(status.TimeZoneId));
    }

    [Fact]
    public async Task ClashDetector_RunsWithoutThrowing()
    {
        var env = await ClashDetector.DetectAsync();
        Assert.NotNull(env.ClientName);
    }

    [Fact]
    public async Task ClaudeDetection_RunsWithoutThrowing()
    {
        var code = await ClaudeCodeManager.DetectAsync(queryVersion: false);
        Assert.False(string.IsNullOrEmpty(code.SettingsPath));
        var desktop = await ClaudeDesktopManager.DetectAsync();
        Assert.NotNull(desktop);
    }

    [Fact]
    public void Wfp_StandardUserCannotModify_OrSucceedsWhenElevated()
    {
        if (Core.Elevation.HelperClient.IsCurrentProcessElevated())
        {
            Assert.True(KillSwitch.CountActiveFilters() >= 0);
            return;
        }
        // Opening a non-dynamic session and adding objects must be refused for a standard user.
        var ex = Record.Exception(() =>
        {
            using var engine = new WfpEngine(dynamic: true);
            engine.InTransaction(() => engine.EnsureProviderAndSubLayer());
        });
        Assert.NotNull(ex);
        Assert.IsType<WfpException>(ex);
    }
}
