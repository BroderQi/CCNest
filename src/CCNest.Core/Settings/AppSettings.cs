using CCNest.Core.Proxy;

namespace CCNest.Core.Settings;

public enum FirstStageMode
{
    /// <summary>Chain through the local port of an existing Clash/Mihomo client (default).</summary>
    ClashLocalPort,
    /// <summary>Use a single Clash/Mihomo proxy node definition directly inside CCNest's core.</summary>
    ImportedNode,
    /// <summary>A manually entered HTTP/SOCKS5 upstream proxy.</summary>
    ManualProxy,
    /// <summary>No first stage: CCNest connects to the static proxy directly.</summary>
    None,
}

public enum ProtectionLevel
{
    /// <summary>Explicit proxy configuration only (browser flag, system proxy, Claude Code env). No admin.</summary>
    Standard,
    /// <summary>Standard + WFP rules that only allow Claude apps to reach the network via CCNest. Admin.</summary>
    AppEnforced,
    /// <summary>CCNest owns the TUN device and a WFP kill switch blocks everything that bypasses it. Admin.</summary>
    SystemTun,
}

public enum RoutingScope
{
    /// <summary>All traffic entering CCNest goes through the second stage (recommended).</summary>
    AllTraffic,
    /// <summary>Only Claude/Anthropic domains go through the second stage; other traffic uses the first stage.</summary>
    ClaudeOnly,
}

public enum CrashPolicy
{
    /// <summary>Safety first: if CCNest dies unexpectedly, keep traffic blocked until the user decides.</summary>
    KeepBlocked,
    /// <summary>Availability first: automatically restore the original network settings.</summary>
    AutoRestore,
}

public enum CloseAction
{
    Ask,
    MinimizeToTray,
    ExitAndRestore,
}

public enum BrowserKind
{
    Auto,
    Chrome,
    Edge,
    Brave,
    Chromium,
    Custom,
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public FirstStageSettings FirstStage { get; set; } = new();
    public SecondStageSettings SecondStage { get; set; } = new();
    public ProtectionSettings Protection { get; set; } = new();
    public IntegrationSettings Integrations { get; set; } = new();
    public SystemEnvSettings SystemEnv { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
    public MonitorSettings Monitor { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
}

public sealed class FirstStageSettings
{
    public FirstStageMode Mode { get; set; } = FirstStageMode.ClashLocalPort;

    // ClashLocalPort
    public string LocalHost { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; } = 7897;
    public ProxyProtocol LocalProtocol { get; set; } = ProxyProtocol.Socks5;

    /// <summary>Controller address: "127.0.0.1:9097" or a named pipe "\\.\pipe\verge-mihomo". Auto-detected when empty.</summary>
    public string? ControllerAddress { get; set; }

    /// <summary>Optional: during the session, pin this selector group in the existing client to <see cref="LockNode"/>.</summary>
    public string? LockGroup { get; set; }
    public string? LockNode { get; set; }

    // ManualProxy (password stored in SecretStore)
    public ProxyEndpoint Manual { get; set; } = new() { Protocol = ProxyProtocol.Socks5, Port = 1080 };

    // ImportedNode: YAML stored in SecretStore; only the display name is kept here.
    public string? ImportedNodeName { get; set; }
    public string? ImportedNodeType { get; set; }
}

public sealed class SecondStageSettings
{
    public string Provider { get; set; } = "custom";
    public string DisplayName { get; set; } = "静态住宅代理";
    public ProxyEndpoint Endpoint { get; set; } = new() { Protocol = ProxyProtocol.Socks5 };

    /// <summary>The exit IP the user expects (their purchased static IP). Strongly recommended.</summary>
    public string? ExpectedExitIp { get; set; }
}

public sealed class ProtectionSettings
{
    public ProtectionLevel Level { get; set; } = ProtectionLevel.Standard;
    public RoutingScope Scope { get; set; } = RoutingScope.AllTraffic;
    public CrashPolicy CrashPolicy { get; set; } = CrashPolicy.KeepBlocked;

    /// <summary>Block all CCNest traffic when the observed exit IP differs from the expected IP.</summary>
    public bool BlockOnExitMismatch { get; set; } = true;

    /// <summary>Close the gate on sleep/network change until the exit is verified again.</summary>
    public bool ReverifyOnWakeOrNetworkChange { get; set; } = true;

    public bool AllowLan { get; set; } = true;

    /// <summary>Require the expected exit IP to be configured before activation.</summary>
    public bool RequireExpectedIp { get; set; } = true;
}

public sealed class IntegrationSettings
{
    /// <summary>Point the Windows (WinINet) system proxy at CCNest during the session.</summary>
    public bool SetSystemProxy { get; set; } = true;

    /// <summary>Merge HTTPS_PROXY/HTTP_PROXY into ~/.claude/settings.json "env" during the session.</summary>
    public bool ConfigureClaudeCode { get; set; } = true;

    /// <summary>Set HTTP(S)_PROXY as user environment variables during the session (affects all new programs).</summary>
    public bool SetUserEnvironmentProxy { get; set; }

    /// <summary>In TUN mode, temporarily turn off the existing client's TUN through its controller.</summary>
    public bool DisableExistingClientTun { get; set; } = true;
}

public sealed class SystemEnvSettings
{
    /// <summary>Apply the selected settings on activation and restore them on deactivation.</summary>
    public bool ApplyWithSession { get; set; }

    public bool RegionalFormat { get; set; } = true;
    public string RegionalFormatLocale { get; set; } = "en-US";

    public bool HomeLocation { get; set; }
    public int HomeGeoId { get; set; } = 244; // United States

    public bool LanguageList { get; set; } = true;
    public string PreferredLanguage { get; set; } = "en-US";

    public bool DisplayLanguage { get; set; }

    public bool TimeZone { get; set; }
    public string TimeZoneId { get; set; } = "Pacific Standard Time";
}

public sealed class BrowserSettings
{
    public BrowserKind Preferred { get; set; } = BrowserKind.Auto;
    public string? CustomPath { get; set; }
    public string AcceptLanguages { get; set; } = "en-US,en";
    public string UiLanguage { get; set; } = "en-US";
    public bool RestrictWebRtcToProxy { get; set; } = true;
}

public sealed class MonitorSettings
{
    public int HealthIntervalSeconds { get; set; } = 20;
    public int ExitIpIntervalSeconds { get; set; } = 120;
    public int FailureThreshold { get; set; } = 2;
}

public sealed class UiSettings
{
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;
    public bool FirstRunCompleted { get; set; }
    public bool AcceptedNotice { get; set; }
    public bool SkipActivationPreview { get; set; }
    public string Theme { get; set; } = "System";
}

public sealed class AdvancedSettings
{
    public int MixedPort { get; set; } = 17897;
    public string? MihomoPathOverride { get; set; }

    /// <summary>Release feed override. Empty = the official GitHub releases of BroderQi/CCNest.</summary>
    public string UpdateFeedUrl { get; set; } = "";
}

public sealed class UpdateSettings
{
    /// <summary>Check GitHub for a new release at start-up and twice a day while running.</summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>A version the user chose to skip: automatic checks stay quiet about it.</summary>
    public string? SkippedVersion { get; set; }

    public DateTime? LastCheckUtc { get; set; }

    /// <summary>Version that ran last time, used to confirm a finished update.</summary>
    public string? LastRunVersion { get; set; }
}
