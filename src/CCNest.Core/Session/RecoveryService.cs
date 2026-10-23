using System.Diagnostics;
using CCNest.Core.Elevation;
using CCNest.Core.Infrastructure;
using CCNest.Core.Settings;

namespace CCNest.Core.Session;

/// <summary>
/// Crash recovery. The lifecycle design resolves the conflict between "automatically restore" and
/// "fail closed" explicitly, through the user's crash policy:
///
///  * KeepBlocked (default): if CCNest dies, nothing is restored automatically. Apps pointed at
///    CCNest's port fail (the core dies with CCNest via a job object), WFP rules stay (non-dynamic
///    session), and a lockdown rule set replaces system-wide rules. CCNest is relaunched (and also
///    registered to start at next login) to let the user restore or re-activate. A reboot clears the
///    WFP rules but not the proxy settings, which keep pointing at the dead port (still fail-closed
///    for proxy-aware apps) until the user decides.
///  * AutoRestore: the watchdog restores user-level settings immediately and the elevated helper
///    removes its WFP rules; Claude traffic then uses the original (unprotected) network.
/// </summary>
public static class RecoveryService
{
    /// <summary>Runs in the "--watchdog" process; waits for the UI to exit and applies the crash policy.</summary>
    public static async Task<int> RunWatchdogAsync(int parentPid, string sessionId)
    {
        Log.ProcessTag = "watchdog";
        if (parentPid <= 4) return 0; // 0 = Idle, 4 = System: never a CCNest parent
        Process parent;
        try { parent = Process.GetProcessById(parentPid); }
        catch { return 0; }

        await parent.WaitForExitAsync().ConfigureAwait(false);
        await Task.Delay(2000).ConfigureAwait(false); // allow a clean shutdown to finish the journal

        var journal = JournalStore.LoadSession();
        if (journal == null || journal.SessionId != sessionId || journal.Status == SessionStatus.Closed || journal.CleanShutdown)
            return 0;

        Log.Warn("Watchdog", $"CCNest 主程序意外退出（会话 {sessionId}，策略 {journal.CrashPolicy}）");

        // The user-mode core was in a kill-on-close job: it is gone.
        foreach (var c in journal.Changes.OfType<SidecarChange>()) c.State = ChangeState.Restored;

        if (journal.CrashPolicy == CrashPolicy.AutoRestore)
        {
            var executor = new ChangeExecutor(broker: null);
            var userLevel = journal.Changes.Where(c => !c.RequiresElevation && c.NeedsRestore).ToList();
            await executor.RestoreAllAsync(userLevel, () => JournalStore.SaveSession(journal)).ConfigureAwait(false);
            // Dynamic WFP rules and the TUN core disappear together with the elevated helper.
            foreach (var c in journal.Changes.Where(c => c.RequiresElevation && c.NeedsRestore))
                if (c is TunCoreChange || c is KillSwitchChange { Dynamic: true }) c.State = ChangeState.Restored;
            JournalStore.SaveSession(journal);
            if (!journal.HasPendingRestore) JournalStore.CloseSession(journal);
        }
        else
        {
            JournalStore.SaveSession(journal);
        }

        RelaunchForRecovery();
        return 0;
    }

    public static void RelaunchForRecovery()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && exe.EndsWith("CCNest.exe", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(exe, "--recover") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("Watchdog", $"无法重新启动 CCNest：{ex.Message}");
        }
    }

    /// <summary>Restores every change of an orphaned (crashed) session. May show a UAC prompt for WFP rules.</summary>
    public static async Task<IReadOnlyList<RestoreOutcome>> RestoreOrphanedAsync(SessionJournal journal, bool force = false, CancellationToken ct = default)
    {
        HelperClient? helper = null;
        try
        {
            foreach (var c in journal.Changes.OfType<SidecarChange>().Where(c => c.NeedsRestore))
            {
                KillIfMihomo(c.Pid);
                c.State = ChangeState.Restored;
            }
            foreach (var c in journal.Changes.OfType<TunCoreChange>().Where(c => c.NeedsRestore))
                c.State = ChangeState.Restored; // dies with the helper (job object)

            if (journal.Changes.Any(c => c is KillSwitchChange && c.NeedsRestore))
            {
                helper = new HelperClient();
                try
                {
                    await helper.EnsureStartedAsync(CrashPolicy.AutoRestore, ct).ConfigureAwait(false);
                }
                catch (ElevationDeniedException)
                {
                    Log.Warn("Recovery", "用户拒绝管理员授权，WFP 规则保持（重启电脑也会清除）");
                }
            }

            var executor = new ChangeExecutor(helper);
            var outcomes = await executor.RestoreAllAsync(journal.Changes, () => JournalStore.SaveSession(journal), force, ct).ConfigureAwait(false);
            if (helper is { IsConnected: true }) await helper.ShutdownAsync(restore: true, ct).ConfigureAwait(false);
            journal.CleanShutdown = !journal.HasPendingRestore;
            if (!journal.HasPendingRestore) JournalStore.CloseSession(journal);
            else JournalStore.SaveSession(journal);
            return outcomes;
        }
        finally
        {
            if (helper != null) await helper.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void KillIfMihomo(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.ProcessName.Equals("mihomo", StringComparison.OrdinalIgnoreCase)) p.Kill();
        }
        catch { /* already gone */ }
    }

    /// <summary>Elevated one-shot used by "--restore-network": remove any CCNest WFP rules.</summary>
    public static int RemoveLeftoverFirewallRules()
    {
        try
        {
            var removed = Wfp.KillSwitch.RemoveAll();
            Log.Info("Recovery", $"已移除 {removed} 条残留 WFP 规则");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Recovery", "移除 WFP 规则失败", ex);
            return 1;
        }
    }
}
