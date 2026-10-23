using System.Collections.Concurrent;
using System.Text;

namespace CCNest.Core.Infrastructure;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() =>
        $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-5}] {Source}: {Message}";
}

/// <summary>
/// Minimal thread-safe logger. Every message passes through <see cref="SecretRedactor"/>
/// before it reaches memory or disk, so credentials never appear in logs.
/// </summary>
public static class Log
{
    private const int RingSize = 3000;
    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    private static readonly object FileLock = new();
    private static string? _fileOverride;
    public static bool FileEnabled { get; set; } = true;
    public static string ProcessTag { get; set; } = "ui";

    public static event Action<LogEntry>? EntryWritten;

    public static IReadOnlyList<LogEntry> Snapshot() => Ring.ToArray();

    public static string CurrentFile => _fileOverride ?? Path.Combine(AppPaths.LogsDir, $"ccnest-{DateTime.Now:yyyyMMdd}.log");

    public static void OverrideFile(string? path) => _fileOverride = path;

    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public static void Warn(string source, string message) => Write(LogLevel.Warn, source, message);
    public static void Error(string source, string message, Exception? ex = null) =>
        Write(LogLevel.Error, source, ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, SecretRedactor.Redact(message));
        Ring.Enqueue(entry);
        while (Ring.Count > RingSize && Ring.TryDequeue(out _)) { }

        if (FileEnabled)
        {
            try
            {
                lock (FileLock)
                {
                    File.AppendAllText(CurrentFile, $"{entry} <{ProcessTag}:{Environment.ProcessId}>{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never crash the app.
            }
        }

        try { EntryWritten?.Invoke(entry); } catch { /* ignore subscriber failures */ }
    }

    /// <summary>Deletes log files older than the retention window.</summary>
    public static void Prune(int keepDays = 14)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDir, "ccnest-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-keepDays))
                    File.Delete(file);
            }
        }
        catch { /* ignore */ }
    }
}
