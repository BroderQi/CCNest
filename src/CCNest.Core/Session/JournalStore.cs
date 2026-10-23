using System.Diagnostics;
using CCNest.Core.Infrastructure;

namespace CCNest.Core.Session;

/// <summary>Durable storage for the session journal and the system-environment journal.</summary>
public static class JournalStore
{
    private static readonly object Lock = new();

    public static SessionJournal? LoadSession() => AtomicFile.ReadJson<SessionJournal>(AppPaths.SessionJournalFile);

    public static void SaveSession(SessionJournal journal)
    {
        lock (Lock) AtomicFile.WriteJson(AppPaths.SessionJournalFile, journal);
    }

    /// <summary>Archives a finished session journal (kept for diagnostics) and removes the live file.</summary>
    public static void CloseSession(SessionJournal journal)
    {
        lock (Lock)
        {
            journal.Status = SessionStatus.Closed;
            try
            {
                var archive = Path.Combine(AppPaths.StateDir, "history");
                Directory.CreateDirectory(archive);
                AtomicFile.WriteJson(Path.Combine(archive, $"session-{journal.StartedAt:yyyyMMdd-HHmmss}-{journal.SessionId}.json"), journal);
                foreach (var old in Directory.GetFiles(archive, "session-*.json").OrderByDescending(f => f).Skip(30))
                    File.Delete(old);
            }
            catch (Exception ex)
            {
                Log.Warn("Journal", $"归档会话日志失败：{ex.Message}");
            }
            if (File.Exists(AppPaths.SessionJournalFile)) File.Delete(AppPaths.SessionJournalFile);
        }
    }

    public static SystemEnvJournal LoadSystemEnv() => AtomicFile.ReadJson<SystemEnvJournal>(AppPaths.SystemEnvJournalFile) ?? new SystemEnvJournal();

    public static void SaveSystemEnv(SystemEnvJournal journal)
    {
        lock (Lock)
        {
            journal.Changes.RemoveAll(c => c.State is ChangeState.Restored);
            AtomicFile.WriteJson(AppPaths.SystemEnvJournalFile, journal);
        }
    }

    /// <summary>True when the process that owns the journal is still alive (guards against PID reuse).</summary>
    public static bool IsOwnerAlive(SessionJournal journal)
    {
        try
        {
            using var p = Process.GetProcessById(journal.OwnerPid);
            return Math.Abs((p.StartTime - journal.OwnerStartTime).TotalSeconds) < 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A session journal that still needs attention and whose owner is gone = crashed session.</summary>
    public static SessionJournal? FindOrphanedSession()
    {
        var j = LoadSession();
        if (j == null || j.Status == SessionStatus.Closed) return null;
        if (j.OwnerPid == Environment.ProcessId) return null;
        if (IsOwnerAlive(j)) return null;
        return j.HasPendingRestore || j.Status != SessionStatus.Closed ? j : null;
    }
}
