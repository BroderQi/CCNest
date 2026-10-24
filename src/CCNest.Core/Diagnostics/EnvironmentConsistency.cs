using System.Globalization;
using CCNest.Core.Browser;
using CCNest.Core.Proxy;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;

namespace CCNest.Core.Diagnostics;

public sealed record ConsistencyAssessment(IReadOnlyList<DiagnosticItem> Checks)
{
    public int Score => Checks.Where(c => c.Status == DiagStatus.Ok).Sum(c => c.ConsistencyWeight ?? 0);
    public int PendingPoints => Checks.Where(c => c.Status == DiagStatus.Unknown).Sum(c => c.ConsistencyWeight ?? 0);
    public int RiskCount => Checks.Count(c => c.Status == DiagStatus.Warning);
    public int PendingCount => Checks.Count(c => c.Status == DiagStatus.Unknown);
    public string ScoreText => PendingCount > 0 ? $"已确认 {Score}/100 分（检测未完成）" : $"{Score}/100 分";
    public string Summary => RiskCount > 0 ? $"风险：{RiskCount} 项不一致" + (PendingCount > 0 ? $"，{PendingCount} 项待确认" : "")
        : PendingCount > 0 ? $"待检测：{PendingCount} 项尚未确认" : "正常：浏览器、Windows 与 IP 出口一致";
}

/// <summary>Local, explicit consistency rules, independent of any provider's IP reputation score.</summary>
public static class EnvironmentConsistency
{
    public static ConsistencyAssessment Evaluate(AppSettings settings, RegionalStatus? windows,
        BrowserCheckResult? browser, IpIntel? intel, string? observedExitIp)
    {
        var checks = new List<DiagnosticItem>();
        var language = FirstLanguage(browser);
        var preferred = windows?.LanguageTags.FirstOrDefault();
        var configured = settings.Browser.AcceptLanguages.Split(',')[0].Split(';')[0].Trim();
        var sources = intel?.Sources.Where(s => s.Ok).ToList() ?? new List<IpIntelSource>();
        var countries = sources.Select(s => NormalizeCountry(s.CountryCode)).Where(c => c != null).Distinct().ToList();
        var country = countries.Count == 1 ? countries[0] : null;
        var zones = sources.Select(s => s.Timezone).Where(z => !string.IsNullOrWhiteSpace(z)).ToList();
        var ipZone = zones.Count > 0 && zones.All(z => ZonesMatch(z, zones[0]) == true) ? zones[0] : null;
        var sourceDetails = sources.Select(s => new KeyValuePair<string, string>(s.Name,
            $"国家 {s.CountryCode ?? "未知"}；时区 {s.Timezone ?? "未知"}")).ToList();
        var countryUnknown = countries.Count > 1 ? "IP 数据源对国家的判断不一致，暂不据此扣分。" : "尚未取得出口国家，请先完成线路检测。";
        var zoneUnknown = zones.Count > 1 ? "IP 数据源的时区存在分歧或无效，暂不据此扣分。" : "尚未取得有效的出口时区，请先完成线路检测。";

        void Add(string id, string title, int weight, bool? match, string actual, string fix, string? pending = null,
            IReadOnlyList<KeyValuePair<string, string>>? details = null)
        {
            checks.Add(new DiagnosticItem
            {
                Id = "consistency-" + id, Category = DiagCategory.Consistency, Title = title,
                Status = match == true ? DiagStatus.Ok : match == false ? DiagStatus.Warning : DiagStatus.Unknown,
                Detected = match == true ? "正常：" + actual : match == false ? "风险：" + actual : "待检测：" + (pending ?? "缺少实际检测数据，请激活环境后点击“开始检测”。"),
                ManualSteps = match == false ? fix : null,
                Explanation = match == false ? $"此项不一致，扣 {weight} 分。" : match == true ? $"此项一致，计 {weight} 分。" : $"此项 {weight} 分尚未确认，不按正常或风险处理。",
                ConsistencyWeight = weight, Details = details ?? Array.Empty<KeyValuePair<string, string>>(),
            });
        }

        bool? exitMatch = browser?.BrowserExitIp == null || observedExitIp == null ? null
            : IpEchoService.SameIp(browser.BrowserExitIp, observedExitIp)
              && (string.IsNullOrWhiteSpace(settings.SecondStage.ExpectedExitIp) || IpEchoService.SameIp(browser.BrowserExitIp, settings.SecondStage.ExpectedExitIp));
        Add("exit", "浏览器与专属线路出口", 15, exitMatch,
            $"浏览器 {browser?.BrowserExitIp}；实测线路 {observedExitIp}；期望 {settings.SecondStage.ExpectedExitIp ?? "未设置"}",
            "检查代理线路与期望出口 IP，并从 CCNest 重新打开专属浏览器。",
            "需要本次线路实测出口和专属浏览器出口；未获取到出口不能判定一致。");
        Add("language-config", "浏览器语言与配置", 5, LocalesMatch(language, configured),
            $"浏览器 {language}；配置 {configured}", "在“专属浏览器”中调整语言偏好，关闭专属浏览器所有窗口后重新打开。");
        Add("language-windows", "浏览器与 Windows 首选语言", 10, LocalesMatch(language, preferred),
            $"浏览器 {language}；Windows 首选 {preferred}", "在“系统环境”中调整 Windows 首选语言，应用后重新打开专属浏览器。");
        Add("format", "浏览器与 Windows 区域格式", 10, LocalesMatch(browser?.Locale, windows?.Culture),
            $"浏览器 {browser?.Locale}；Windows 区域格式 {windows?.Culture}", "在“系统环境”中调整区域格式或浏览器语言，应用后重新打开专属浏览器。");
        Add("display-language", "浏览器与 Windows 显示语言", 5, LocalesMatch(language, windows?.CurrentUiLanguage),
            $"浏览器 {language}；Windows 当前显示语言 {windows?.CurrentUiLanguage}",
            "在 Windows“语言和区域”中选择相应显示语言；显示语言更改需注销后生效。已设置的覆盖值不等于当前已生效语言。");

        var browserWindowsZone = ZonesMatch(browser?.TimeZone, windows?.TimeZoneId);
        if (browserWindowsZone == true)
        {
            var offset = OffsetAt(windows?.TimeZoneId, browser!.CapturedAt);
            browserWindowsZone = browser.TzOffsetMinutes.HasValue && offset.HasValue ? browser.TzOffsetMinutes.Value == offset.Value : null;
        }
        Add("timezone-windows", "浏览器与 Windows 时区", 10, browserWindowsZone,
            $"浏览器 {browser?.TimeZone}（UTC 偏移 {browser?.TzOffsetMinutes} 分钟）；Windows {windows?.TimeZoneId}",
            "在“系统环境”中调整时区后重新打开专属浏览器，避免使用缓存了旧时区的窗口。");
        Add("timezone-ip", "浏览器时区与 IP 出口", 20, ZonesMatch(browser?.TimeZone, ipZone),
            $"浏览器 {browser?.TimeZone}；IP 出口 {ipZone}", "按出口所在地时区调整 Windows 时区，并重新打开专属浏览器。",
            browser == null ? null : zoneUnknown, sourceDetails);
        Add("windows-timezone-ip", "Windows 时区与 IP 出口", 10, ZonesMatch(windows?.TimeZoneId, ipZone),
            $"Windows {windows?.TimeZoneId}；IP 出口 {ipZone}", "在“系统环境”中将 Windows 时区调整为出口所在地时区。",
            windows == null ? "Windows 区域设置读取失败，请重新检测。" : zoneUnknown, sourceDetails);
        Add("home-country", "Windows 地区与 IP 出口", 5, CountriesMatch(windows?.CountryCode, country),
            $"Windows {windows?.CountryCode}；IP 出口 {country}", "在“系统环境”中调整 Windows 国家或地区。",
            windows == null ? "Windows 国家或地区读取失败。" : countryUnknown, sourceDetails);
        Add("language-country", "浏览器语言地区与 IP 出口", 5, CountriesMatch(LocaleCountry(language), country),
            $"浏览器 {language}；IP 出口 {country}", "按你的目标地区调整专属浏览器首选语言，例如美国出口使用 en-US。",
            browser == null ? null : country == null ? countryUnknown : "首选语言缺少国家/地区，例如 en 无法与国家比较；请使用 en-US 等完整标记。", sourceDetails);
        Add("format-country", "Windows 区域格式与 IP 出口", 5, CountriesMatch(LocaleCountry(windows?.Culture), country),
            $"Windows 区域格式 {windows?.Culture}；IP 出口 {country}", "在“系统环境”中将 Windows 区域格式调整为目标地区。",
            windows == null ? "Windows 区域设置读取失败。" : country == null ? countryUnknown : "Windows 区域格式缺少有效的国家/地区信息。", sourceDetails);
        return new ConsistencyAssessment(checks);
    }

    private static string? FirstLanguage(BrowserCheckResult? browser) => browser?.Languages.FirstOrDefault() ?? browser?.Language;
    private static bool? CountriesMatch(string? a, string? b) => NormalizeCountry(a) is not { } left || NormalizeCountry(b) is not { } right
        ? null : left == right;
    private static string? NormalizeCountry(string? value) => value?.Trim() is { Length: 2 } code && code.All(char.IsAsciiLetter) ? code.ToUpperInvariant() : null;
    private static bool? LocalesMatch(string? a, string? b) => string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) ? null
        : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string? LocaleCountry(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        try
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            return culture.IsNeutralCulture ? null : NormalizeCountry(new RegionInfo(culture.Name).TwoLetterISORegionName);
        }
        catch (ArgumentException) { return null; }
    }

    internal static bool? ZonesMatch(string? a, string? b)
    {
        var left = ResolveZone(a);
        var right = ResolveZone(b);
        if (left == null || right == null) return null;
        // Windows and browsers use different ID vocabularies. Map IDs, never infer equality from
        // today's offset alone (Los Angeles and Phoenix coincide in winter but differ in summer).
        var leftId = CanonicalZone(left.Value.Zone.Id);
        var rightId = CanonicalZone(right.Value.Zone.Id);
        return leftId.Equals(rightId, StringComparison.OrdinalIgnoreCase)
            && (left.Value.NoDst == right.Value.NoDst || !left.Value.Zone.SupportsDaylightSavingTime);
    }

    private static string CanonicalZone(string id) => TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) ? windows : id;
    private static (TimeZoneInfo Zone, bool NoDst)? ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var noDst = id.EndsWith("_dstoff", StringComparison.OrdinalIgnoreCase);
        try { return (TimeZoneInfo.FindSystemTimeZoneById(noDst ? id[..^7] : id), noDst); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    private static int? OffsetAt(string? id, DateTimeOffset at) => ResolveZone(id) is { } zone
        ? (int)(zone.NoDst ? zone.Zone.BaseUtcOffset : zone.Zone.GetUtcOffset(at)).TotalMinutes : null;
}
