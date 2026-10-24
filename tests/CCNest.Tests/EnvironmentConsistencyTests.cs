using System.Text.Json;
using CCNest.Core.Browser;
using CCNest.Core.Diagnostics;
using CCNest.Core.Proxy;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;

namespace CCNest.Tests;

public class EnvironmentConsistencyTests
{
    private const string Exit = "198.51.100.20";
    private static RegionalStatus Windows => new()
    {
        Culture = "en-US", CountryCode = "US", LanguageTags = new[] { "en-US", "zh-CN" },
        CurrentUiLanguage = "en-US", TimeZoneId = "Pacific Standard Time",
    };
    private static BrowserCheckResult Browser
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return new()
            {
                Languages = new() { "en-US", "en" }, Locale = "en-US", TimeZone = "America/Los_Angeles",
                TzOffsetMinutes = (int)TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time").GetUtcOffset(now).TotalMinutes,
                BrowserExitIp = Exit, CapturedAt = now,
            };
        }
    }
    private static IpIntelSource Source(string name = "IPQuery", string country = "US", string zone = "America/Los_Angeles") =>
        new(name, true, null, country, "Los Angeles", "California", zone, "AS1", "Example", null, null, false, false, false, false);
    private static IpIntel Intel => new(Exit, new[] { Source(), Source("IPLocate") });
    private static ConsistencyAssessment Assess(RegionalStatus? windows, BrowserCheckResult? browser, IpIntel? intel) =>
        EnvironmentConsistency.Evaluate(new AppSettings(), windows, browser, intel, Exit);

    [Fact]
    public void FullyMeasuredConsistentEnvironment_Scores100()
    {
        var result = Assess(Windows, Browser, Intel);
        Assert.Equal(100, result.Checks.Sum(c => c.ConsistencyWeight));
        Assert.Equal(100, result.Score);
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(0, result.RiskCount);
        Assert.All(result.Checks, c => Assert.Equal(DiagStatus.Ok, c.Status));
        Assert.StartsWith("正常", result.Summary);
    }

    [Fact]
    public void MissingBrowser_IsPending_NotNormalOrRisk()
    {
        var result = Assess(Windows, null, Intel);
        Assert.Equal(20, result.Score); // Windows timezone, country, and format are still measured.
        Assert.Equal(80, result.PendingPoints);
        Assert.Equal(0, result.RiskCount);
        Assert.Contains("检测未完成", result.ScoreText);
    }

    [Fact]
    public void MissingIpData_DoesNotProduceFalseFullScore()
    {
        var result = Assess(Windows, Browser, null);
        Assert.Equal(55, result.Score);
        Assert.Equal(45, result.PendingPoints);
        Assert.Equal(0, result.RiskCount);
    }

    [Fact]
    public void DisplayLanguageMismatch_IsAnExplicitDeduction_WithActualValues()
    {
        var result = Assess(Windows with { CurrentUiLanguage = "zh-CN", UiLanguageOverride = "en-US" }, Browser, Intel);
        Assert.Equal(95, result.Score);
        var risk = Assert.Single(result.Checks, c => c.Status == DiagStatus.Warning);
        Assert.Contains("zh-CN", risk.Detected);
        Assert.Contains("en-US", risk.Detected);
        Assert.Contains("注销", risk.ManualSteps);
        Assert.StartsWith("风险", result.Summary);
    }

    [Fact]
    public void ExitMismatch_DeductsAndGivesRepairSteps()
    {
        var result = Assess(Windows, Browser with { BrowserExitIp = "198.51.100.21" }, Intel);
        Assert.Equal(85, result.Score);
        var risk = Assert.Single(result.Checks, c => c.Status == DiagStatus.Warning);
        Assert.Equal("consistency-exit", risk.Id);
        Assert.NotNull(risk.ManualSteps);
    }

    [Fact]
    public void GeographyDisagreement_IsPending_NotAnArbitraryCountryPenalty()
    {
        var intel = new IpIntel(Exit, new[] { Source(), Source("IPLocate", "CA", "America/New_York") });
        var result = Assess(Windows, Browser, intel);
        Assert.Equal(55, result.Score);
        Assert.Equal(45, result.PendingPoints);
        Assert.Equal(0, result.RiskCount);
        Assert.Contains(result.Checks, c => c.Detected.Contains("分歧"));
    }

    [Fact]
    public void MissingOffset_IsNotAssumedToBeUtc()
    {
        var result = Assess(Windows, Browser with { TzOffsetMinutes = null }, Intel);
        Assert.Equal(DiagStatus.Unknown, result.Checks.Single(c => c.Id == "consistency-timezone-windows").Status);
        Assert.Equal(90, result.Score);
    }

    [Theory]
    [InlineData("Pacific Standard Time", "America/Los_Angeles", true)]
    [InlineData("America/Phoenix", "America/Los_Angeles", false)]
    [InlineData("America/New_York", "America/Los_Angeles", false)]
    [InlineData("Pacific Standard Time_dstoff", "America/Los_Angeles", false)]
    [InlineData("not-a-timezone", "America/Los_Angeles", null)]
    [InlineData(null, "America/Los_Angeles", null)]
    public void Timezones_UseZoneIdentityAndDst_NotJustCurrentOffset(string? a, string? b, bool? match) =>
        Assert.Equal(match, EnvironmentConsistency.ZonesMatch(a, b));

    [Fact]
    public void NeutralLanguage_DoesNotGuessItsCountry()
    {
        var result = Assess(Windows, Browser with { Languages = new() { "en" } }, Intel);
        Assert.Equal(DiagStatus.Unknown, result.Checks.Single(c => c.Id == "consistency-language-country").Status);
    }

    [Fact]
    public void Export_IncludesScoreDeductionAndPendingRules()
    {
        var score = Assess(Windows with { CurrentUiLanguage = "zh-CN" }, Browser, Intel);
        var report = new DiagnosticReport(DateTime.Now, TimeSpan.Zero, score.Checks) { Consistency = score };
        var text = report.ToText();
        Assert.Contains("95/100", text);
        Assert.Contains("扣 5 分", text);
        Assert.Contains("未知项目不计分", text);
    }
}

public class IpIntelTests
{
    [Fact]
    public void IpQuery_ParsesMeasuredFlagsAndAttributedScore()
    {
        using var json = JsonDocument.Parse("""
            {"ip":"66.93.67.1","isp":{"asn":"AS3257","org":"GTT","isp":"GTT"},
             "location":{"country_code":"US","city":"Los Angeles","state":"California","timezone":"America/Los_Angeles"},
             "risk":{"is_mobile":false,"is_vpn":false,"is_proxy":false,"is_tor":false,"is_datacenter":false,"risk_score":0}}
            """);
        var source = IpIntelService.ParseIpQuery("IPQuery", json.RootElement);
        Assert.True(source.Ok);
        Assert.False(source.IsDatacenter);
        Assert.False(source.IsVpn);
        Assert.Equal(0, source.RiskScore);
        Assert.Equal("America/Los_Angeles", source.Timezone);
        Assert.Equal(IpClass.Unknown, new IpIntel("66.93.67.1", new[] { source }).Classification);
    }

    [Fact]
    public void IpLocate_UsesCompanyType_NotBroadAsnType()
    {
        using var json = JsonDocument.Parse("""
            {"ip":"66.93.67.1","country_code":"US","time_zone":"America/New_York",
             "asn":{"asn":"AS3257","name":"GTT","type":"isp"},
             "company":{"name":"Bunny","type":"hosting"},
             "privacy":{"is_hosting":true,"is_vpn":false,"is_proxy":false,"is_tor":false}}
            """);
        var source = IpIntelService.ParseIpLocate("IPLocate", json.RootElement);
        Assert.Equal("hosting", source.Type);
        Assert.Equal("Bunny", source.Org);
        Assert.Equal(IpClass.Datacenter, new IpIntel("66.93.67.1", new[] { source }).Classification);
    }

    [Fact]
    public void MissingFlagsAndInvalidScore_RemainUnknown()
    {
        using var json = JsonDocument.Parse("""{"location":{"country_code":"US"},"risk":{"risk_score":"0"}}""");
        var source = IpIntelService.ParseIpQuery("IPQuery", json.RootElement);
        Assert.Null(source.IsDatacenter);
        Assert.Null(source.IsVpn);
        Assert.Null(source.RiskScore);
    }

    [Fact]
    public void ClassificationDisagreement_IsRetained()
    {
        var a = new IpIntelSource("IPQuery", true, null, "US", null, null, null, null, null, null, null, false, false, false, false);
        var b = a with { Name = "IPLocate", IsDatacenter = true, Type = "hosting" };
        var intel = new IpIntel("66.93.67.1", new[] { a, b });
        Assert.True(intel.ClassificationDisagreement);
        Assert.Equal(IpClass.Unknown, intel.Classification);
    }
}
