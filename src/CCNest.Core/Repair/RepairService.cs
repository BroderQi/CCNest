using CCNest.Core.Claude;
using CCNest.Core.Diagnostics;
using CCNest.Core.Elevation;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;

namespace CCNest.Core.Repair;

/// <summary>
/// The controlled set of supported repairs. Each repair: inspects current state, records the original
/// value in the standalone journal (so it appears in the restore center and can be undone), applies a
/// precise change, and verifies the result. Nothing outside this list is ever executed.
/// </summary>
public static class RepairService
{
    /// <summary>A loopback proxy port that nothing is listening on is "dead": traffic using it fails.</summary>
    public static bool IsDeadLoopbackPort(int port) => NetworkInspector.IsPortFree(port);

    public static RepairAction ClearSystemProxy(SystemProxyState current, string reason) => new(
        "clear-system-proxy",
        "恢复系统代理为直连",
        reason,
        new[] { $"Windows 系统代理：{current.Describe()} → 不使用代理", "原设置会记录在恢复中心，可随时撤销" },
        false,
        _ =>
        {
            var applied = current with { ProxyEnabled = false };
            Record(new SystemProxyChange { Original = current, Applied = applied }, () => SystemProxy.Backend.Write(applied));
            var after = SystemProxy.Backend.Read();
            return Task.FromResult(after.ProxyEnabled ? "写入后系统代理仍处于开启状态，可能被其他程序改回" : "系统代理已恢复为直连");
        });

    public static RepairAction RemoveUserEnvVars(IReadOnlyList<EnvVarValue> vars, string reason) => new(
        "remove-env-proxy",
        "删除失效的代理环境变量",
        reason,
        vars.Select(v => $"删除用户环境变量 {v.Name}={v.Value}").Append("对新启动的程序生效；原值记录在恢复中心").ToList(),
        false,
        _ =>
        {
            foreach (var v in vars)
                Record(new UserEnvChange { Name = v.Name, Original = v.Value, Applied = null }, () => EnvironmentVars.SetUser(v.Name, null));
            return Task.FromResult($"已删除 {vars.Count} 个用户环境变量（新启动的程序生效）");
        });

    public static RepairAction RemoveClaudeCodeProxy(string path, IReadOnlyDictionary<string, string> current, string reason) => new(
        "remove-claude-proxy",
        "移除 Claude Code 中失效的代理设置",
        reason,
        current.Select(kv => $"{path}：删除 env.{kv.Key}={kv.Value}").Append("其他设置保持不变；修改前自动备份").ToList(),
        false,
        _ =>
        {
            var original = ClaudeCodeManager.ReadManagedEnv(path);
            var applied = original.ToDictionary(kv => kv.Key, kv => current.ContainsKey(kv.Key) ? null : kv.Value);
            var envExisted = File.Exists(path) && ClaudeCodeManager.ReadSettings(path)["env"] is System.Text.Json.Nodes.JsonObject;
            Record(new ClaudeCodeEnvChange { SettingsPath = path, FileExisted = true, EnvExisted = envExisted, Original = original, Applied = applied },
                () => ClaudeCodeManager.WriteManagedEnv(path, applied.Where(kv => current.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), removeEmptyEnv: true));
            return Task.FromResult("已移除失效的代理设置（新启动的 Claude Code 生效）");
        });

    public static RepairAction RemoveLeftoverWfpRules(int count) => new(
        "remove-wfp",
        "移除残留的网络阻断规则",
        "上次会话的 CCNest 防泄漏规则仍在生效，可能导致部分程序无法联网。",
        new[] { $"删除 {(count > 0 ? count.ToString() : "所有")} 条 CCNest WFP 规则（需要管理员授权）" },
        true,
        async ct =>
        {
            await using var helper = new HelperClient();
            await helper.EnsureStartedAsync(CrashPolicy.AutoRestore, ct).ConfigureAwait(false);
            await helper.RemoveKillSwitchAsync(ct).ConfigureAwait(false);
            var remaining = await helper.KillSwitchFilterCountAsync(ct).ConfigureAwait(false);
            await helper.ShutdownAsync(restore: false, ct).ConfigureAwait(false);
            return remaining == 0 ? "残留规则已全部移除" : $"仍有 {remaining} 条规则未能移除";
        });

    public static RepairAction RestoreOrphanedSession(SessionJournal journal) => new(
        "restore-orphan",
        "恢复上次异常结束的会话所做的更改",
        "CCNest 上次没有正常退出，部分设置仍处于激活状态。",
        journal.Changes.Where(c => c.NeedsRestore).Select(c => $"{c.Title}：{c.Describe}").ToList(),
        journal.Changes.Any(c => c.RequiresElevation && c.NeedsRestore),
        async ct =>
        {
            var outcomes = await RecoveryService.RestoreOrphanedAsync(journal, false, ct).ConfigureAwait(false);
            var failed = outcomes.Count(o => !o.Success);
            return failed == 0 ? "上次会话的更改已全部恢复" : $"{failed} 项未能恢复，请在恢复中心查看详情";
        });

    private static void Record(ChangeRecord change, Action apply)
    {
        var journal = JournalStore.LoadSystemEnv();
        journal.Changes.Add(change);
        JournalStore.SaveSystemEnv(journal);
        try
        {
            apply();
            change.State = ChangeState.Applied;
        }
        catch
        {
            journal.Changes.Remove(change);
            JournalStore.SaveSystemEnv(journal);
            throw;
        }
        JournalStore.SaveSystemEnv(journal);
        Log.Info("Repair", $"{change.Title}: {change.Describe}");
    }
}
