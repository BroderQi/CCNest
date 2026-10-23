using System.Text.Json.Serialization;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;
using CCNest.Core.Wfp;

namespace CCNest.Core.Session;

public enum ChangeState
{
    /// <summary>Recorded before being applied (write-ahead). Restoring a pending change is a no-op-safe check.</summary>
    Pending,
    Applied,
    Restored,
    RestoreFailed,
    /// <summary>Someone else changed the value after CCNest applied it; CCNest left it alone.</summary>
    Conflict,
    /// <summary>Restore succeeded but only takes effect after sign-out / restart.</summary>
    RestoredPendingReboot,
}

/// <summary>
/// A single reversible modification made by CCNest. Records are journaled to disk *before* the change
/// is applied, together with the original value, so a crash at any point can be recovered.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SystemProxyChange), "systemProxy")]
[JsonDerivedType(typeof(ClaudeCodeEnvChange), "claudeCodeEnv")]
[JsonDerivedType(typeof(UserEnvChange), "userEnv")]
[JsonDerivedType(typeof(ClashTunChange), "clashTun")]
[JsonDerivedType(typeof(ClashSelectionChange), "clashSelection")]
[JsonDerivedType(typeof(CultureChange), "culture")]
[JsonDerivedType(typeof(GeoChange), "geo")]
[JsonDerivedType(typeof(LanguageListChange), "languages")]
[JsonDerivedType(typeof(UiLanguageChange), "uiLanguage")]
[JsonDerivedType(typeof(TimeZoneChange), "timeZone")]
[JsonDerivedType(typeof(KillSwitchChange), "killSwitch")]
[JsonDerivedType(typeof(TunCoreChange), "tunCore")]
[JsonDerivedType(typeof(SidecarChange), "sidecar")]
[JsonDerivedType(typeof(RecoveryStartupChange), "recoveryStartup")]
public abstract class ChangeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public ChangeState State { get; set; } = ChangeState.Pending;
    public DateTime RecordedAt { get; set; } = DateTime.Now;
    public string? LastError { get; set; }

    [JsonIgnore] public abstract string Title { get; }
    [JsonIgnore] public virtual EffectTiming ApplyTiming => EffectTiming.Immediate;
    [JsonIgnore] public virtual EffectTiming RestoreTiming => ApplyTiming;
    [JsonIgnore] public virtual bool RequiresElevation => false;
    [JsonIgnore] public abstract string Describe { get; }

    [JsonIgnore] public bool NeedsRestore => State is ChangeState.Applied or ChangeState.Pending or ChangeState.RestoreFailed;
}

public sealed class SystemProxyChange : ChangeRecord
{
    public SystemProxyState Original { get; set; } = new();
    public SystemProxyState Applied { get; set; } = new();
    public override string Title => "Windows 系统代理";
    public override string Describe => $"{Original.Describe()} → {Applied.Describe()}";
}

public sealed class ClaudeCodeEnvChange : ChangeRecord
{
    public string SettingsPath { get; set; } = "";
    public bool FileExisted { get; set; }
    public bool EnvExisted { get; set; }
    public Dictionary<string, string?> Original { get; set; } = new();
    public Dictionary<string, string?> Applied { get; set; } = new();
    public override string Title => "Claude Code 代理配置";
    public override EffectTiming ApplyTiming => EffectTiming.NewProcesses;
    public override string Describe => $"{SettingsPath} 中的 env.HTTPS_PROXY / HTTP_PROXY / NO_PROXY";
}

public sealed class UserEnvChange : ChangeRecord
{
    public string Name { get; set; } = "";
    public string? Original { get; set; }
    public string? Applied { get; set; }
    public override string Title => $"用户环境变量 {Name}";
    public override EffectTiming ApplyTiming => EffectTiming.NewProcesses;
    public override string Describe => $"{Original ?? "(未设置)"} → {Applied ?? "(删除)"}";
}

public sealed class ClashTunChange : ChangeRecord
{
    public string ControllerAddress { get; set; } = "";
    public bool OriginalEnabled { get; set; }
    public bool AppliedEnabled { get; set; }
    public override string Title => "现有 Clash 客户端的 TUN 模式";
    public override string Describe => $"{(OriginalEnabled ? "开启" : "关闭")} → {(AppliedEnabled ? "开启" : "关闭")}（通过控制接口临时修改，不改动配置文件）";
}

public sealed class ClashSelectionChange : ChangeRecord
{
    public string ControllerAddress { get; set; } = "";
    public string Group { get; set; } = "";
    public string? OriginalNode { get; set; }
    public string AppliedNode { get; set; } = "";
    public override string Title => $"一级线路节点（{Group}）";
    public override string Describe => $"{OriginalNode ?? "?"} → {AppliedNode}";
}

public sealed class CultureChange : ChangeRecord
{
    public RegionalSnapshot Snapshot { get; set; } = new();
    public string AppliedCulture { get; set; } = "";
    public override string Title => "区域格式";
    public override EffectTiming ApplyTiming => EffectTiming.NewProcesses;
    public override string Describe => $"{Snapshot.Culture} → {AppliedCulture}";
}

public sealed class GeoChange : ChangeRecord
{
    public int Original { get; set; }
    public int Applied { get; set; }
    public override string Title => "国家或地区（主位置）";
    public override string Describe => $"{Original} → {Applied}";
}

public sealed class LanguageListChange : ChangeRecord
{
    public List<LanguageEntry> Original { get; set; } = new();
    public string AppliedTop { get; set; } = "";
    public override string Title => "首选语言顺序";
    public override EffectTiming ApplyTiming => EffectTiming.NewProcesses;
    public override string Describe => $"{string.Join(", ", Original.Select(l => l.Tag))} → {AppliedTop} 置顶";
}

public sealed class UiLanguageChange : ChangeRecord
{
    public string? Original { get; set; }
    public string Applied { get; set; } = "";
    public override string Title => "Windows 显示语言";
    public override EffectTiming ApplyTiming => EffectTiming.SignOut;
    public override string Describe => $"{Original ?? "(跟随系统)"} → {Applied}";
}

public sealed class TimeZoneChange : ChangeRecord
{
    public string Original { get; set; } = "";
    public string Applied { get; set; } = "";
    public override string Title => "时区";
    public override string Describe => $"{Original} → {Applied}";
}

public sealed class KillSwitchChange : ChangeRecord
{
    public KillSwitchMode Mode { get; set; }
    public bool Dynamic { get; set; }
    public override string Title => Mode switch
    {
        KillSwitchMode.AppScoped => "Claude 应用联网限制（WFP）",
        KillSwitchMode.SystemWide => "全局防泄漏阻断（WFP）",
        _ => "网络锁定（WFP）",
    };
    public override bool RequiresElevation => true;
    public override string Describe => Dynamic ? "CCNest 退出时自动撤销" : "CCNest 意外退出后保持阻断，直到恢复或重启电脑";
}

public sealed class TunCoreChange : ChangeRecord
{
    public string Device { get; set; } = "CCNest";
    public override string Title => "CCNest 虚拟网卡（TUN）";
    public override bool RequiresElevation => true;
    public override string Describe => $"由提权助手运行的 TUN 内核（网卡 {Device}）";
}

public sealed class SidecarChange : ChangeRecord
{
    public int Pid { get; set; }
    public int MixedPort { get; set; }
    public override string Title => "CCNest 代理内核";
    public override string Describe => $"本地端口 127.0.0.1:{MixedPort}";
}

public sealed class RecoveryStartupChange : ChangeRecord
{
    public override string Title => "登录时自动检查恢复";
    public override string Describe => "会话期间临时添加开机启动项，正常结束后删除";
}

public enum SessionStatus
{
    Activating,
    Active,
    Deactivating,
    Closed,
}

/// <summary>Write-ahead journal of an environment session (state\session.json).</summary>
public sealed class SessionJournal
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public int OwnerPid { get; set; } = Environment.ProcessId;
    public DateTime OwnerStartTime { get; set; } = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    public SessionStatus Status { get; set; } = SessionStatus.Activating;
    public ProtectionLevel Level { get; set; }
    public RoutingScope Scope { get; set; }
    public CrashPolicy CrashPolicy { get; set; }
    public int MixedPort { get; set; }
    public string? ExpectedExitIp { get; set; }
    public string? VerifiedExitIp { get; set; }
    public bool CleanShutdown { get; set; }
    public List<ChangeRecord> Changes { get; set; } = new();

    [JsonIgnore] public bool HasPendingRestore => Changes.Any(c => c.NeedsRestore);
}

/// <summary>Journal of Windows environment changes applied outside a session (state\system-env.json).</summary>
public sealed class SystemEnvJournal
{
    public List<ChangeRecord> Changes { get; set; } = new();
}
