using System.Text.RegularExpressions;

namespace CCNest.Core.Proxy;

/// <summary>
/// Recognises username conventions that residential-proxy providers use for rotating or time-limited
/// "sticky" sessions (e.g. "...-sid-abc123-t-120"). Such endpoints are not fixed static IPs: the exit
/// changes when the session expires, which CCNest will (correctly) treat as an exit change and block.
/// This is a heuristic based on common naming patterns; it can be wrong and is presented as a hint.
/// </summary>
public static partial class EndpointHeuristics
{
    public sealed record Finding(bool LooksSticky, bool LooksGeoTargetedPool, int? SessionMinutes, string Message);

    public static Finding? Analyze(ProxyEndpoint ep)
    {
        var user = ep.Username ?? "";
        if (user.Length == 0) return null;

        var sticky = StickyRegex().IsMatch(user);
        var geo = GeoRegex().IsMatch(user);
        int? minutes = null;
        var t = DurationRegex().Match(user);
        if (t.Success && int.TryParse(t.Groups["n"].Value, out var n) && n is > 0 and < 100_000) minutes = n;

        if (!sticky && !geo && minutes == null) return null;

        var parts = new List<string>();
        if (sticky || minutes != null)
            parts.Add(minutes != null
                ? $"用户名中似乎包含限时粘性会话参数（可能为 {minutes} 分钟），会话到期后服务商会分配新的出口 IP"
                : "用户名中似乎包含会话（session）参数，这通常是轮换住宅代理的“粘性会话”，出口 IP 会在会话结束后变化");
        if (geo)
            parts.Add("用户名中包含地区定向参数，通常表示从住宅 IP 池中按地区分配，而不是固定的静态 IP");
        parts.Add("出口变化时 CCNest 会按设计阻断流量。如需长期固定的出口，请向服务商确认购买的是静态（Static / ISP）IP 及其专用端点");
        return new Finding(sticky || minutes != null, geo, minutes, "（推测）" + string.Join("；", parts) + "。");
    }

    [GeneratedRegex(@"(^|[-_])(sid|session|sessid|sess|sessionid)[-_]?[A-Za-z0-9]{3,}", RegexOptions.IgnoreCase)]
    private static partial Regex StickyRegex();

    [GeneratedRegex(@"(^|[-_])(t|sesstime|lifetime|life|sessionduration|ttl)[-_](?<n>\d{1,5})($|[-_])", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"(^|[-_])(region|country|cc|st|state|city|zip)[-_][A-Za-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex GeoRegex();
}
