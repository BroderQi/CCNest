using System.Runtime.InteropServices;
using System.Text.Json;
using CCNest.Core.Infrastructure;
using Microsoft.Win32;

namespace CCNest.Core.SystemEnv;

/// <summary>When a Windows setting change becomes visible.</summary>
public enum EffectTiming
{
    Immediate,
    NewProcesses,
    SignOut,
    Restart,
}

public sealed record LanguageEntry(string Tag, IReadOnlyList<string> InputMethodTips);

/// <summary>A complete snapshot of the user-level regional settings CCNest can change.</summary>
public sealed record RegionalSnapshot
{
    public string Culture { get; init; } = "";
    public Dictionary<string, string> InternationalValues { get; init; } = new();
    public int GeoId { get; init; }
    public List<LanguageEntry> Languages { get; init; } = new();
    public string? UiLanguageOverride { get; init; }
    public string TimeZoneId { get; init; } = "";
    public DateTime CapturedAt { get; init; } = DateTime.Now;
}

public sealed record RegionalStatus
{
    public string Culture { get; init; } = "";
    public int GeoId { get; init; }
    public string GeoName { get; init; } = "";
    public IReadOnlyList<string> LanguageTags { get; init; } = Array.Empty<string>();
    public string? UiLanguageOverride { get; init; }
    public string CurrentUiLanguage { get; init; } = "";
    public IReadOnlyList<string> InstalledUiLanguages { get; init; } = Array.Empty<string>();
    public string TimeZoneId { get; init; } = "";
    public string TimeZoneDisplay { get; init; } = "";
    public bool AutoTimeZoneEnabled { get; init; }
    public string SystemLocale { get; init; } = "";
}

/// <summary>
/// User-level Windows regional settings. Only per-user settings are changed (no system locale,
/// fonts, drivers or language-pack installation). All operations use documented cmdlets of the
/// built-in International module or documented Win32 APIs; values are passed as environment
/// variables, never concatenated into script text.
///
/// Effect timing (verified behaviour of Windows 10/11):
///   * Regional format (Set-Culture): new processes immediately; running apps may need restart.
///   * Home location (SetUserGeoID): immediate.
///   * Preferred language list (Set-WinUserLanguageList): new processes; affects Accept-Language of
///     apps that use the Windows list (Edge, UWP). Chrome keeps its own language preference.
///   * Display language override: requires sign-out, both to apply and to restore, and only works
///     when that display language is installed.
///   * Time zone (tzutil): immediate system-wide; some running apps cache the zone until restart.
/// </summary>
public static class RegionalSettings
{
    private const string InternationalKey = @"Control Panel\International";

    public static async Task<RegionalSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        const string script = """
            $list = Get-WinUserLanguageList
            $langs = @(foreach ($l in $list) { [pscustomobject]@{ Tag = $l.LanguageTag; Tips = @($l.InputMethodTips) } })
            $ui = Get-WinUILanguageOverride
            [pscustomobject]@{
              Culture = (Get-Culture).Name
              Languages = $langs
              UiOverride = $(if ($ui) { $ui.Name } else { $null })
              TimeZone = (& "$env:SystemRoot\System32\tzutil.exe" /g)
            } | ConvertTo-Json -Depth 5 -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script, ct: ct).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException("读取区域设置失败：" + (r.StdErr.Length > 0 ? r.StdErr : r.StdOut));
        using var doc = JsonDocument.Parse(r.StdOut.Trim());
        var root = doc.RootElement;
        var langs = new List<LanguageEntry>();
        if (root.TryGetProperty("Languages", out var la))
        {
            var items = la.ValueKind == JsonValueKind.Array ? la.EnumerateArray().ToList() : new List<JsonElement> { la };
            foreach (var item in items)
            {
                var tag = item.GetProperty("Tag").GetString() ?? "";
                var tips = new List<string>();
                if (item.TryGetProperty("Tips", out var t))
                {
                    if (t.ValueKind == JsonValueKind.Array) tips.AddRange(t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
                    else if (t.ValueKind == JsonValueKind.String) tips.Add(t.GetString()!);
                }
                if (tag.Length > 0) langs.Add(new LanguageEntry(tag, tips));
            }
        }

        return new RegionalSnapshot
        {
            Culture = root.GetProperty("Culture").GetString() ?? "",
            InternationalValues = ReadInternationalValues(),
            GeoId = GetUserGeoID(GEOCLASS_NATION),
            Languages = langs,
            UiLanguageOverride = root.TryGetProperty("UiOverride", out var ui) && ui.ValueKind == JsonValueKind.String ? ui.GetString() : null,
            TimeZoneId = root.GetProperty("TimeZone").GetString()?.Trim() ?? TimeZoneInfo.Local.Id,
        };
    }

    public static async Task<RegionalStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        var snap = await CaptureAsync(ct).ConfigureAwait(false);
        var tzBase = snap.TimeZoneId.Replace("_dstoff", "", StringComparison.OrdinalIgnoreCase);
        string tzDisplay;
        try { tzDisplay = TimeZoneInfo.FindSystemTimeZoneById(tzBase).DisplayName; }
        catch { tzDisplay = snap.TimeZoneId; }
        string geoName;
        try { geoName = GetGeoName(snap.GeoId); }
        catch { geoName = snap.GeoId.ToString(); }

        return new RegionalStatus
        {
            Culture = snap.Culture,
            GeoId = snap.GeoId,
            GeoName = geoName,
            LanguageTags = snap.Languages.Select(l => l.Tag).ToList(),
            UiLanguageOverride = snap.UiLanguageOverride,
            CurrentUiLanguage = System.Globalization.CultureInfo.CurrentUICulture.Name,
            InstalledUiLanguages = InstalledUiLanguages(),
            TimeZoneId = snap.TimeZoneId,
            TimeZoneDisplay = tzDisplay,
            AutoTimeZoneEnabled = IsAutoTimeZoneEnabled(),
            SystemLocale = System.Globalization.CultureInfo.InstalledUICulture.Name,
        };
    }

    public static Dictionary<string, string> ReadInternationalValues()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var k = Registry.CurrentUser.OpenSubKey(InternationalKey);
        if (k == null) return dict;
        foreach (var name in k.GetValueNames())
            if (k.GetValue(name) is string s) dict[name] = s;
        return dict;
    }

    public static IReadOnlyList<string> InstalledUiLanguages()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MUI\UILanguages");
            return k?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static bool IsAutoTimeZoneEnabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\tzautoupdate");
            return (k?.GetValue("Start") as int?) == 3;
        }
        catch
        {
            return false;
        }
    }

    // ---- Regional format ---------------------------------------------------------------

    public static async Task ApplyCultureAsync(string culture, CancellationToken ct = default)
    {
        ValidateTag(culture);
        await RunAsync("Set-Culture -CultureInfo $env:CCNEST_VALUE", culture, ct).ConfigureAwait(false);
        EnvironmentVars.Broadcast("intl");
    }

    /// <summary>
    /// Restores the original culture, then re-applies any individual customisations (e.g. a custom
    /// short date format) that Set-Culture reset, using the documented SetLocaleInfo API.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RestoreCultureAsync(RegionalSnapshot snap, CancellationToken ct = default)
    {
        var problems = new List<string>();
        await RunAsync("Set-Culture -CultureInfo $env:CCNEST_VALUE", snap.Culture, ct).ConfigureAwait(false);
        var now = ReadInternationalValues();
        foreach (var (name, original) in snap.InternationalValues)
        {
            if (!LcTypes.TryGetValue(name, out var lctype)) continue;
            if (now.TryGetValue(name, out var current) && current == original) continue;
            if (!SetLocaleInfo(LOCALE_USER_DEFAULT, lctype, original))
                problems.Add($"{name}（错误 {Marshal.GetLastWin32Error()}）");
        }
        EnvironmentVars.Broadcast("intl");
        return problems;
    }

    // ---- Home location ----------------------------------------------------------------

    public static void ApplyGeoId(int geoId)
    {
        if (!SetUserGeoID(geoId)) throw new InvalidOperationException($"SetUserGeoID 失败（{Marshal.GetLastWin32Error()}）");
        EnvironmentVars.Broadcast("intl");
    }

    public static int CurrentGeoId() => GetUserGeoID(GEOCLASS_NATION);

    public static string GetGeoName(int geoId)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetGeoInfo(geoId, GEO_FRIENDLYNAME, sb, sb.Capacity, 0) > 0 ? sb.ToString() : geoId.ToString();
    }

    // ---- Preferred languages ------------------------------------------------------------

    public static async Task MoveLanguageToTopAsync(string tag, CancellationToken ct = default)
    {
        ValidateTag(tag);
        const string script = """
            $target = $env:CCNEST_VALUE
            $list = Get-WinUserLanguageList
            $existing = $null
            foreach ($l in $list) { if ($l.LanguageTag -ieq $target) { $existing = $l } }
            if ($existing) { [void]$list.Remove($existing); $list.Insert(0, $existing) }
            else { $new = New-WinUserLanguageList $target; $list.Insert(0, $new[0]) }
            Set-WinUserLanguageList $list -Force
            """;
        await RunAsync(script, tag, ct).ConfigureAwait(false);
    }

    public static async Task RestoreLanguagesAsync(IReadOnlyList<LanguageEntry> languages, CancellationToken ct = default)
    {
        if (languages.Count == 0) return;
        foreach (var l in languages) ValidateTag(l.Tag);
        const string script = """
            $saved = @($env:CCNEST_VALUE | ConvertFrom-Json)
            $list = New-WinUserLanguageList $saved[0].Tag
            for ($i = 1; $i -lt $saved.Count; $i++) { $list.Add($saved[$i].Tag) }
            for ($i = 0; $i -lt $saved.Count; $i++) {
              $tips = @($saved[$i].Tips)
              if ($tips.Count -gt 0) {
                $list[$i].InputMethodTips.Clear()
                foreach ($t in $tips) { try { $list[$i].InputMethodTips.Add($t) } catch { } }
              }
            }
            Set-WinUserLanguageList $list -Force
            """;
        var json = JsonSerializer.Serialize(languages.Select(l => new { l.Tag, Tips = l.InputMethodTips }));
        await RunAsync(script, json, ct).ConfigureAwait(false);
    }

    // ---- Display language (requires sign-out) ----------------------------------------------

    public static async Task SetUiLanguageOverrideAsync(string? tag, CancellationToken ct = default)
    {
        if (tag == null)
        {
            await RunAsync("Set-WinUILanguageOverride", "", ct).ConfigureAwait(false);
            return;
        }
        ValidateTag(tag);
        await RunAsync("Set-WinUILanguageOverride -Language $env:CCNEST_VALUE", tag, ct).ConfigureAwait(false);
    }

    // ---- Time zone ------------------------------------------------------------------------

    public static async Task SetTimeZoneAsync(string id, CancellationToken ct = default)
    {
        var baseId = id.Replace("_dstoff", "", StringComparison.OrdinalIgnoreCase);
        TimeZoneInfo.FindSystemTimeZoneById(baseId); // throws for unknown ids
        var r = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "tzutil.exe"), new[] { "/s", id },
            TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException($"设置时区失败：{r.StdOut}{r.StdErr}".Trim());
        TimeZoneInfo.ClearCachedData();
    }

    public static string CurrentTimeZoneId()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tzutil.exe"), "/g")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return output.Length > 0 ? output : TimeZoneInfo.Local.Id;
        }
        catch
        {
            return TimeZoneInfo.Local.Id;
        }
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static async Task RunAsync(string script, string value, CancellationToken ct)
    {
        var r = await ProcessRunner.PowerShellAsync(script, new Dictionary<string, string?> { ["CCNEST_VALUE"] = value }, ct: ct).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException((r.StdErr.Length > 0 ? r.StdErr : r.StdOut).Trim());
    }

    private static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 85 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException($"无效的语言标记：{tag}");
    }

    /// <summary>Registry value name → LCTYPE accepted by SetLocaleInfo.</summary>
    internal static readonly Dictionary<string, uint> LcTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sShortDate"] = 0x1F, ["sLongDate"] = 0x20, ["sTimeFormat"] = 0x1003, ["sShortTime"] = 0x79, ["sYearMonth"] = 0x1006,
        ["sDecimal"] = 0x0E, ["sThousand"] = 0x0F, ["sGrouping"] = 0x10, ["sList"] = 0x0C, ["iMeasure"] = 0x0D,
        ["sCurrency"] = 0x14, ["sMonDecimalSep"] = 0x16, ["sMonThousandSep"] = 0x17, ["sMonGrouping"] = 0x18,
        ["iCurrDigits"] = 0x19, ["iCurrency"] = 0x1B, ["iNegCurr"] = 0x1C, ["iDigits"] = 0x11, ["iLZero"] = 0x12,
        ["iNegNumber"] = 0x1010, ["sNegativeSign"] = 0x51, ["sPositiveSign"] = 0x50, ["s1159"] = 0x28, ["s2359"] = 0x29,
        ["iFirstDayOfWeek"] = 0x100C, ["iFirstWeekOfYear"] = 0x100D, ["sNativeDigits"] = 0x13, ["iCalendarType"] = 0x1009,
        ["iPaperSize"] = 0x100A, ["NumShape"] = 0x1014,
    };

    private const int GEOCLASS_NATION = 16;
    private const int GEO_FRIENDLYNAME = 8;
    private const uint LOCALE_USER_DEFAULT = 0x0400;

    [DllImport("kernel32.dll")]
    private static extern int GetUserGeoID(int geoClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetUserGeoID(int geoId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetGeoInfo(int location, int geoType, System.Text.StringBuilder data, int size, int langId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetLocaleInfoW")]
    private static extern bool SetLocaleInfo(uint locale, uint lctype, string data);
}
