using System.Runtime.InteropServices;

namespace CCNest.Core.SystemEnv;

public sealed record FirewallInfo
{
    public bool? DomainEnabled { get; init; }
    public bool? PrivateEnabled { get; init; }
    public bool? PublicEnabled { get; init; }
    public string? CurrentProfiles { get; init; }
    public ServiceState FirewallService { get; init; }
    public ServiceState BaseFilteringEngine { get; init; }
    public string? Error { get; init; }

    /// <summary>Windows Defender Firewall is off for at least one active profile.</summary>
    public bool DefenderDisabledSomewhere => DomainEnabled == false || PrivateEnabled == false || PublicEnabled == false
                                             || FirewallService != ServiceState.Running;

    /// <summary>WFP filters (CCNest's enforcement) only need the Base Filtering Engine, not Defender Firewall.</summary>
    public bool WfpAvailable => BaseFilteringEngine == ServiceState.Running;
}

public enum ServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
}

/// <summary>
/// Read-only inspection of Windows Defender Firewall and the Base Filtering Engine (BFE).
/// CCNest's enforcement uses its own WFP filters, which work even when Defender Firewall is turned
/// off, as long as the BFE service runs (it is required by Windows and normally always running).
/// </summary>
public static class FirewallStatus
{
    public static FirewallInfo Read()
    {
        var bfe = QueryService("BFE");
        var mps = QueryService("MpsSvc");
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return new FirewallInfo { FirewallService = mps, BaseFilteringEngine = bfe, Error = "无法访问防火墙 COM 接口" };
            dynamic policy = Activator.CreateInstance(type)!;
            bool? Get(int profile)
            {
                try { return (bool)policy.FirewallEnabled[profile]; }
                catch { return null; }
            }
            int current = 0;
            try { current = (int)policy.CurrentProfileTypes; } catch { /* ignore */ }
            var names = new List<string>();
            if ((current & 1) != 0) names.Add("域");
            if ((current & 2) != 0) names.Add("专用");
            if ((current & 4) != 0) names.Add("公用");
            return new FirewallInfo
            {
                DomainEnabled = Get(1),
                PrivateEnabled = Get(2),
                PublicEnabled = Get(4),
                CurrentProfiles = string.Join("、", names),
                FirewallService = mps,
                BaseFilteringEngine = bfe,
            };
        }
        catch (Exception ex)
        {
            return new FirewallInfo { FirewallService = mps, BaseFilteringEngine = bfe, Error = ex.Message };
        }
    }

    public static ServiceState QueryService(string name)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return ServiceState.Unknown;
        try
        {
            var svc = OpenService(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060 ? ServiceState.NotInstalled : ServiceState.Unknown;
            try
            {
                return QueryServiceStatus(svc, out var status) ? (ServiceState)Math.Clamp(status.dwCurrentState + 1, 0, 8) : ServiceState.Unknown;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr h);
}
