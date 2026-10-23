using CCNest.Core.Claude;
using CCNest.Core.Infrastructure;
using CCNest.Core.Mihomo;
using CCNest.Core.SystemEnv;

namespace CCNest.Core.Session;

public sealed record RestoreOutcome(ChangeRecord Change, bool Success, string Message, EffectTiming Timing);

/// <summary>Elevated operations, implemented by the elevated helper process.</summary>
public interface IElevationBroker
{
    bool IsAvailable { get; }
    Task RemoveKillSwitchAsync(CancellationToken ct = default);
    Task StopTunCoreAsync(CancellationToken ct = default);
}

/// <summary>
/// Restores journaled changes. Every restore first checks whether the current value is still the
/// value CCNest applied; if the user or another program changed it in the meantime, CCNest leaves it
/// alone and reports a conflict instead of overwriting the newer value.
/// </summary>
public sealed class ChangeExecutor
{
    private readonly IElevationBroker? _broker;
    private readonly Func<Task>? _stopSidecar;

    public ChangeExecutor(IElevationBroker? broker, Func<Task>? stopSidecar = null)
    {
        _broker = broker;
        _stopSidecar = stopSidecar;
    }

    public async Task<IReadOnlyList<RestoreOutcome>> RestoreAllAsync(IList<ChangeRecord> changes, Action? persist,
        bool force = false, CancellationToken ct = default)
    {
        var outcomes = new List<RestoreOutcome>();
        foreach (var change in changes.Reverse().ToList())
        {
            if (!change.NeedsRestore && !(force && change.State == ChangeState.Conflict)) continue;
            var outcome = await RestoreAsync(change, force, ct).ConfigureAwait(false);
            outcomes.Add(outcome);
            persist?.Invoke();
        }
        return outcomes;
    }

    public async Task<RestoreOutcome> RestoreAsync(ChangeRecord change, bool force = false, CancellationToken ct = default)
    {
        try
        {
            var (ok, message) = change switch
            {
                SystemProxyChange c => RestoreSystemProxy(c, force),
                ClaudeCodeEnvChange c => RestoreClaudeCode(c, force),
                UserEnvChange c => RestoreUserEnv(c, force),
                ClashTunChange c => await RestoreClashTunAsync(c, ct).ConfigureAwait(false),
                ClashSelectionChange c => await RestoreClashSelectionAsync(c, force, ct).ConfigureAwait(false),
                CultureChange c => await RestoreCultureAsync(c, ct).ConfigureAwait(false),
                GeoChange c => RestoreGeo(c, force),
                LanguageListChange c => await RestoreLanguagesAsync(c, ct).ConfigureAwait(false),
                UiLanguageChange c => await RestoreUiLanguageAsync(c, ct).ConfigureAwait(false),
                TimeZoneChange c => await RestoreTimeZoneAsync(c, force, ct).ConfigureAwait(false),
                KillSwitchChange c => await RestoreKillSwitchAsync(c, ct).ConfigureAwait(false),
                TunCoreChange c => await RestoreTunCoreAsync(c, ct).ConfigureAwait(false),
                SidecarChange c => await RestoreSidecarAsync(c).ConfigureAwait(false),
                RecoveryStartupChange => RestoreStartup(),
                _ => (false, "未知的更改类型"),
            };

            if (ok)
            {
                change.State = change.RestoreTiming is EffectTiming.SignOut or EffectTiming.Restart
                    ? ChangeState.RestoredPendingReboot
                    : ChangeState.Restored;
                change.LastError = null;
            }
            else if (change.State != ChangeState.Conflict)
            {
                change.State = ChangeState.RestoreFailed;
                change.LastError = message;
            }
            Log.Write(ok ? LogLevel.Info : LogLevel.Warn, "Restore", $"{change.Title}: {message}");
            return new RestoreOutcome(change, ok, message, change.RestoreTiming);
        }
        catch (Exception ex)
        {
            change.State = ChangeState.RestoreFailed;
            change.LastError = ex.Message;
            Log.Error("Restore", $"{change.Title} 恢复失败", ex);
            return new RestoreOutcome(change, false, ex.Message, change.RestoreTiming);
        }
    }

    private (bool, string) Conflict(ChangeRecord c, string detail)
    {
        c.State = ChangeState.Conflict;
        c.LastError = detail;
        return (false, "已被其他程序或用户修改，未覆盖：" + detail);
    }

    private (bool, string) RestoreSystemProxy(SystemProxyChange c, bool force)
    {
        var current = SystemProxy.Backend.Read();
        if (c.State == ChangeState.Pending && current.Equivalent(c.Original)) return (true, "未曾修改");
        if (!force && !current.Equivalent(c.Applied) && !current.Equivalent(c.Original))
            return Conflict(c, $"当前为 {current.Describe()}");
        SystemProxy.Backend.Write(c.Original);
        var after = SystemProxy.Backend.Read();
        return after.Equivalent(c.Original) ? (true, $"已恢复为：{c.Original.Describe()}") : (false, $"写入后校验失败，当前为 {after.Describe()}");
    }

    private (bool, string) RestoreClaudeCode(ClaudeCodeEnvChange c, bool force)
    {
        if (!File.Exists(c.SettingsPath))
            return c.FileExisted ? (false, "settings.json 已被删除") : (true, "文件已不存在，无需恢复");

        var current = ClaudeCodeManager.ReadManagedEnv(c.SettingsPath);
        var toWrite = new Dictionary<string, string?>();
        var conflicts = new List<string>();
        foreach (var (key, applied) in c.Applied)
        {
            current.TryGetValue(key, out var now);
            c.Original.TryGetValue(key, out var original);
            if (now == original) continue;                  // already original (or never applied)
            if (now == applied || force) toWrite[key] = original;
            else conflicts.Add($"{key}={now ?? "(未设置)"}");
        }

        if (toWrite.Count > 0)
            ClaudeCodeManager.WriteManagedEnv(c.SettingsPath, toWrite, removeEmptyEnv: !c.EnvExisted);
        if (!c.FileExisted && toWrite.Count > 0)
        {
            try
            {
                var root = ClaudeCodeManager.ReadSettings(c.SettingsPath);
                if (root.Count == 0) File.Delete(c.SettingsPath);
            }
            catch { /* keep the file */ }
        }

        if (conflicts.Count > 0) return Conflict(c, string.Join("，", conflicts));
        return (true, toWrite.Count > 0 ? "已恢复 Claude Code 原有代理设置（新启动的 Claude Code 生效）" : "无需恢复");
    }

    private (bool, string) RestoreUserEnv(UserEnvChange c, bool force)
    {
        var now = EnvironmentVars.GetUser(c.Name);
        if (now == c.Original) return (true, "已是原始值");
        if (!force && now != c.Applied) return Conflict(c, $"当前值 {now ?? "(未设置)"}");
        EnvironmentVars.SetUser(c.Name, c.Original);
        return (true, c.Original == null ? "已删除（新启动的程序生效）" : "已恢复原值（新启动的程序生效）");
    }

    private static MihomoController ControllerFor(string address) =>
        new(address, SecretStore.Get(SecretStore.ClashControllerSecret), TimeSpan.FromSeconds(5));

    private async Task<(bool, string)> RestoreClashTunAsync(ClashTunChange c, CancellationToken ct)
    {
        using var ctl = ControllerFor(c.ControllerAddress);
        var cfg = await ctl.GetConfigsAsync(ct).ConfigureAwait(false);
        var now = cfg["tun"]?["enable"]?.GetValue<bool>();
        if (now == c.OriginalEnabled) return (true, "TUN 状态已是原始状态");
        await ctl.PatchConfigsAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["tun"] = new System.Text.Json.Nodes.JsonObject { ["enable"] = c.OriginalEnabled },
        }, ct).ConfigureAwait(false);
        return (true, c.OriginalEnabled ? "已重新开启现有客户端的 TUN" : "已关闭现有客户端的 TUN");
    }

    private async Task<(bool, string)> RestoreClashSelectionAsync(ClashSelectionChange c, bool force, CancellationToken ct)
    {
        if (c.OriginalNode == null) return (true, "无原始节点记录");
        using var ctl = ControllerFor(c.ControllerAddress);
        var proxies = await ctl.GetProxiesAsync(ct).ConfigureAwait(false);
        if (!proxies.TryGetValue(c.Group, out var group)) return (false, $"找不到策略组 {c.Group}");
        if (group.Now == c.OriginalNode) return (true, "已是原始节点");
        if (!force && group.Now != c.AppliedNode) return Conflict(c, $"当前节点 {group.Now}");
        await ctl.SelectProxyAsync(c.Group, c.OriginalNode, ct).ConfigureAwait(false);
        return (true, $"已切回 {c.OriginalNode}");
    }

    private static async Task<(bool, string)> RestoreCultureAsync(CultureChange c, CancellationToken ct)
    {
        var problems = await RegionalSettings.RestoreCultureAsync(c.Snapshot, ct).ConfigureAwait(false);
        return problems.Count == 0
            ? (true, $"区域格式已恢复为 {c.Snapshot.Culture}（新启动的程序生效）")
            : (true, $"区域格式已恢复为 {c.Snapshot.Culture}，但以下自定义项未能恢复：{string.Join("、", problems)}");
    }

    private (bool, string) RestoreGeo(GeoChange c, bool force)
    {
        var now = RegionalSettings.CurrentGeoId();
        if (now == c.Original) return (true, "已是原始值");
        if (!force && now != c.Applied) return Conflict(c, $"当前为 {now}");
        RegionalSettings.ApplyGeoId(c.Original);
        return (true, "国家或地区已恢复");
    }

    private static async Task<(bool, string)> RestoreLanguagesAsync(LanguageListChange c, CancellationToken ct)
    {
        await RegionalSettings.RestoreLanguagesAsync(c.Original, ct).ConfigureAwait(false);
        return (true, $"首选语言已恢复为 {string.Join(", ", c.Original.Select(l => l.Tag))}");
    }

    private static async Task<(bool, string)> RestoreUiLanguageAsync(UiLanguageChange c, CancellationToken ct)
    {
        await RegionalSettings.SetUiLanguageOverrideAsync(c.Original, ct).ConfigureAwait(false);
        return (true, "显示语言已恢复，需要注销并重新登录 Windows 后生效");
    }

    private async Task<(bool, string)> RestoreTimeZoneAsync(TimeZoneChange c, bool force, CancellationToken ct)
    {
        var now = RegionalSettings.CurrentTimeZoneId();
        if (string.Equals(now, c.Original, StringComparison.OrdinalIgnoreCase)) return (true, "已是原始时区");
        if (!force && !string.Equals(now, c.Applied, StringComparison.OrdinalIgnoreCase)) return Conflict(c, $"当前时区 {now}");
        await RegionalSettings.SetTimeZoneAsync(c.Original, ct).ConfigureAwait(false);
        return (true, $"时区已恢复为 {c.Original}");
    }

    private async Task<(bool, string)> RestoreKillSwitchAsync(KillSwitchChange c, CancellationToken ct)
    {
        if (_broker is not { IsAvailable: true }) return (false, "需要管理员权限才能移除网络阻断规则");
        await _broker.RemoveKillSwitchAsync(ct).ConfigureAwait(false);
        return (true, "已移除 CCNest 的 WFP 阻断规则");
    }

    private async Task<(bool, string)> RestoreTunCoreAsync(TunCoreChange c, CancellationToken ct)
    {
        if (_broker is not { IsAvailable: true })
        {
            // Without the helper the TUN core cannot outlive it (job object), so nothing is left running.
            return (true, "提权助手已退出，TUN 内核已随之停止");
        }
        await _broker.StopTunCoreAsync(ct).ConfigureAwait(false);
        return (true, "TUN 虚拟网卡已关闭");
    }

    private async Task<(bool, string)> RestoreSidecarAsync(SidecarChange c)
    {
        if (_stopSidecar != null) await _stopSidecar().ConfigureAwait(false);
        return (true, "代理内核已停止");
    }

    private static (bool, string) RestoreStartup()
    {
        StartupRegistration.RemoveRecovery();
        return (true, "已移除临时启动项");
    }
}
