using CCNest.Core.Infrastructure;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;

namespace CCNest.Core.Session;

public sealed record PlannedChange(ChangeRecord Change, Func<Task> Apply, string Summary)
{
    public EffectTiming Timing => Change.ApplyTiming;
}

/// <summary>
/// Plans and applies the user-selected Windows environment settings (regional format, home location,
/// preferred languages, display language, time zone). Planning is side-effect free so the UI can show
/// exactly what will change, and when it takes effect, before anything is modified.
/// </summary>
public static class SystemEnvService
{
    public static async Task<(List<PlannedChange> Changes, List<string> Notes)> PlanAsync(SystemEnvSettings s, CancellationToken ct = default)
    {
        var plan = new List<PlannedChange>();
        var notes = new List<string>();
        var snap = await RegionalSettings.CaptureAsync(ct).ConfigureAwait(false);

        if (s.RegionalFormat && !string.Equals(snap.Culture, s.RegionalFormatLocale, StringComparison.OrdinalIgnoreCase))
        {
            var target = s.RegionalFormatLocale;
            plan.Add(new PlannedChange(new CultureChange { Snapshot = snap, AppliedCulture = target },
                () => RegionalSettings.ApplyCultureAsync(target, ct), $"区域格式：{snap.Culture} → {target}"));
        }

        if (s.HomeLocation && snap.GeoId != s.HomeGeoId)
        {
            var target = s.HomeGeoId;
            plan.Add(new PlannedChange(new GeoChange { Original = snap.GeoId, Applied = target },
                () => { RegionalSettings.ApplyGeoId(target); return Task.CompletedTask; },
                $"国家或地区：{SafeGeo(snap.GeoId)} → {SafeGeo(target)}"));
        }

        if (s.LanguageList && !string.Equals(snap.Languages.FirstOrDefault()?.Tag, s.PreferredLanguage, StringComparison.OrdinalIgnoreCase))
        {
            var target = s.PreferredLanguage;
            plan.Add(new PlannedChange(new LanguageListChange { Original = snap.Languages.ToList(), AppliedTop = target },
                () => RegionalSettings.MoveLanguageToTopAsync(target, ct),
                $"首选语言：{string.Join(", ", snap.Languages.Select(l => l.Tag))} → {target} 置顶"));
        }

        if (s.DisplayLanguage)
        {
            var installed = RegionalSettings.InstalledUiLanguages();
            var target = s.PreferredLanguage;
            if (!installed.Contains(target, StringComparer.OrdinalIgnoreCase))
                notes.Add($"未安装 {target} 显示语言包，无法切换 Windows 显示语言（CCNest 不会自动下载安装语言包）。可在 Windows 设置 › 时间和语言 中安装。");
            else if (!string.Equals(snap.UiLanguageOverride, target, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(System.Globalization.CultureInfo.CurrentUICulture.Name, target, StringComparison.OrdinalIgnoreCase))
                plan.Add(new PlannedChange(new UiLanguageChange { Original = snap.UiLanguageOverride, Applied = target },
                    () => RegionalSettings.SetUiLanguageOverrideAsync(target, ct),
                    $"显示语言：{snap.UiLanguageOverride ?? System.Globalization.CultureInfo.CurrentUICulture.Name} → {target}（需注销）"));
        }

        if (s.TimeZone && !string.Equals(snap.TimeZoneId, s.TimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            var target = s.TimeZoneId;
            plan.Add(new PlannedChange(new TimeZoneChange { Original = snap.TimeZoneId, Applied = target },
                () => RegionalSettings.SetTimeZoneAsync(target, ct), $"时区：{snap.TimeZoneId} → {target}"));
            if (RegionalSettings.IsAutoTimeZoneEnabled())
                notes.Add("Windows 的“自动设置时区”已开启，系统可能会根据位置自动改回时区。如需保持，请在 Windows 设置中关闭该选项（需要管理员权限）。");
        }

        return (plan, notes);
    }

    /// <summary>Applies all planned changes through <paramref name="applyWithJournal"/> (which journals first).</summary>
    public static async Task<List<(string Title, EffectTiming Timing)>> ApplyAsync(SystemEnvSettings s, Func<PlannedChange, Task> applyWithJournal, CancellationToken ct = default)
    {
        var (plan, notes) = await PlanAsync(s, ct).ConfigureAwait(false);
        foreach (var n in notes) Log.Info("SystemEnv", n);
        var result = new List<(string, EffectTiming)>();
        foreach (var change in plan)
        {
            await applyWithJournal(change).ConfigureAwait(false);
            result.Add((change.Change.Title, change.Timing));
        }
        return result;
    }

    /// <summary>Applies a plan outside a session, recording everything in the system-environment journal.</summary>
    public static async Task<List<(PlannedChange Change, string? Error)>> ApplyStandaloneAsync(IReadOnlyList<PlannedChange> plan, CancellationToken ct = default)
    {
        var journal = JournalStore.LoadSystemEnv();
        var results = new List<(PlannedChange, string?)>();
        foreach (var change in plan)
        {
            // Never stack a second record of the same kind: the earliest original value is the one to restore.
            if (journal.Changes.Any(c => c.GetType() == change.Change.GetType() && c.NeedsRestore))
            {
                await change.Apply().ConfigureAwait(false);
                results.Add((change, null));
                continue;
            }
            journal.Changes.Add(change.Change);
            JournalStore.SaveSystemEnv(journal);
            try
            {
                await change.Apply().ConfigureAwait(false);
                change.Change.State = ChangeState.Applied;
                results.Add((change, null));
            }
            catch (Exception ex)
            {
                journal.Changes.Remove(change.Change);
                results.Add((change, ex.Message));
                Log.Error("SystemEnv", $"{change.Change.Title} 应用失败", ex);
            }
            JournalStore.SaveSystemEnv(journal);
        }
        return results;
    }

    public static async Task<IReadOnlyList<RestoreOutcome>> RestoreStandaloneAsync(bool force = false, CancellationToken ct = default)
    {
        var journal = JournalStore.LoadSystemEnv();
        var executor = new ChangeExecutor(null);
        var outcomes = await executor.RestoreAllAsync(journal.Changes, () => JournalStore.SaveSystemEnv(journal), force, ct).ConfigureAwait(false);
        JournalStore.SaveSystemEnv(journal);
        return outcomes;
    }

    public static string Describe(EffectTiming t) => t switch
    {
        EffectTiming.Immediate => "立即生效",
        EffectTiming.NewProcesses => "对新启动的程序立即生效，已打开的程序需重启",
        EffectTiming.SignOut => "需要注销并重新登录后生效",
        EffectTiming.Restart => "需要重启电脑后生效",
        _ => "",
    };

    private static string SafeGeo(int id)
    {
        try { return RegionalSettings.GetGeoName(id); }
        catch { return id.ToString(); }
    }
}
