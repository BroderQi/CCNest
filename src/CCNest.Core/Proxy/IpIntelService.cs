using System.Text.Json;
using CCNest.Core.Infrastructure;

namespace CCNest.Core.Proxy;

public enum IpClass
{
    Unknown,
    ResidentialOrIsp,
    Datacenter,
    Business,
    Mobile,
    Education,
    Government,
}

public sealed record IpIntelSource(string Name, bool Ok, string? Error, string? CountryCode, string? City, string? Region,
    string? Timezone, string? Asn, string? Org, string? Isp, string? Type, bool? IsProxy, bool? IsVpn, bool? IsDatacenter, bool? IsTor)
{
    public int? RiskScore { get; init; }
}

public sealed record IpIntel(string Ip, IReadOnlyList<IpIntelSource> Sources)
{
    private IEnumerable<IpIntelSource> Ok => Sources.Where(s => s.Ok);

    public string? CountryCode => Majority(Ok.Select(s => s.CountryCode));
    public string? City => Ok.Select(s => s.City).FirstOrDefault(c => !string.IsNullOrEmpty(c));
    public string? Region => Ok.Select(s => s.Region).FirstOrDefault(c => !string.IsNullOrEmpty(c));
    public string? Timezone => Majority(Ok.Select(s => s.Timezone));
    public string? Asn => Ok.Select(s => s.Asn).FirstOrDefault(a => !string.IsNullOrEmpty(a));
    public string? Org => Ok.Select(s => s.Org ?? s.Isp).FirstOrDefault(a => !string.IsNullOrEmpty(a));

    /// <summary>Sources disagree on the country (common for freshly re-assigned or proxy IPs).</summary>
    public bool CountryDisagreement => Ok.Select(s => s.CountryCode?.ToUpperInvariant()).Where(c => c != null).Distinct().Count() > 1;

    public bool ClassificationDisagreement => Ok.Select(HostingFlag).Where(v => v.HasValue).Distinct().Count() > 1;
    public IpClass Classification => ClassificationDisagreement ? IpClass.Unknown
        : Ok.Any(s => HostingFlag(s) == true) ? IpClass.Datacenter
        : Ok.Select(Classify).FirstOrDefault(c => c != IpClass.Unknown);

    private static bool? HostingFlag(IpIntelSource s) => s.IsDatacenter == true || s.Type == "hosting" ? true : s.IsDatacenter;
    private static IpClass Classify(IpIntelSource s) => s.Type?.ToLowerInvariant() switch
    {
        "hosting" => IpClass.Datacenter,
        "isp" => IpClass.ResidentialOrIsp,
        "business" => IpClass.Business,
        "education" => IpClass.Education,
        "government" => IpClass.Government,
        "mobile" => IpClass.Mobile,
        _ => IpClass.Unknown,
    };

    public bool AnyProxyFlag => Ok.Any(s => s.IsProxy == true || s.IsVpn == true || s.IsTor == true);

    public static string Describe(IpClass c) => c switch
    {
        IpClass.ResidentialOrIsp => "ISP 网络（未验证住宅）",
        IpClass.Datacenter => "数据中心 / 托管网络",
        IpClass.Business => "企业网络",
        IpClass.Mobile => "移动网络",
        IpClass.Education => "教育网络",
        IpClass.Government => "政府网络",
        _ => "无法判断",
    };

    private static string? Majority(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrEmpty(v)).GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
}

/// <summary>
/// Looks up geolocation, ASN and network type for an IP using several free public databases.
/// Classification is database-derived and may be wrong; the UI presents it as a reference only.
/// </summary>
public static class IpIntelService
{
    private sealed record CacheEntry(DateTimeOffset Expires, IpIntel Intel);
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim QueryLock = new(1, 1);

    public static async Task<IpIntel> LookupAsync(string ip, IReadOnlyList<ProxyEndpoint> route, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var address)) throw new ArgumentException("无效的 IP 地址", nameof(ip));
        ip = address.MapToIPv6().ToString();
        await QueryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(ip, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Intel;
            var queryIp = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
            using var client = ChainHttp.Create(route, timeout);
            var tasks = new[]
            {
                Query(client, "IPQuery", $"https://api.ipquery.io/{Uri.EscapeDataString(queryIp)}", queryIp, ParseIpQuery, ct),
                Query(client, "IPLocate", $"https://iplocate.io/api/lookup/{Uri.EscapeDataString(queryIp)}", queryIp, ParseIpLocate, ct),
            };
            var intel = new IpIntel(queryIp, await Task.WhenAll(tasks).ConfigureAwait(false));
            // Daily keyless quotas are shared by clients using the same public exit. Repeated local
            // diagnostics reuse data; failed/partial responses expire sooner, and retain null flags.
            var lifetime = intel.Sources.All(s => s.Ok) ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(5);
            if (Cache.Count >= 64) Cache.Remove(Cache.MinBy(p => p.Value.Expires).Key);
            Cache[ip] = new CacheEntry(DateTimeOffset.UtcNow + lifetime, intel);
            return intel;
        }
        finally { QueryLock.Release(); }
    }

    private static async Task<IpIntelSource> Query(HttpClient client, string name, string url, string ip, Func<string, JsonElement, IpIntelSource> parse, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Failed(name, $"HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(body);
            if (!IpEchoService.SameIp(Str(doc.RootElement, "ip"), ip)) return Failed(name, "响应中的 IP 与查询目标不一致");
            return parse(name, doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Debug("IpIntel", $"{name} 查询失败: {ChainHttp.Describe(ex)}");
            return Failed(name, ChainHttp.Describe(ex));
        }
    }

    private static IpIntelSource Failed(string name, string error) =>
        new(name, false, error, null, null, null, null, null, null, null, null, null, null, null, null);

    internal static IpIntelSource ParseIpQuery(string name, JsonElement r)
    {
        var loc = Obj(r, "location");
        var isp = Obj(r, "isp");
        var risk = Obj(r, "risk");
        if (loc.ValueKind != JsonValueKind.Object && risk.ValueKind != JsonValueKind.Object) return Failed(name, "响应缺少位置和分类数据");
        return new IpIntelSource(name, true, null,
            Str(loc, "country_code"), Str(loc, "city"), Str(loc, "state"), Str(loc, "timezone"),
            Str(isp, "asn"), Str(isp, "org"), Str(isp, "isp"), Bool(risk, "is_mobile") == true ? "mobile" : null,
            Bool(risk, "is_proxy"), Bool(risk, "is_vpn"), Bool(risk, "is_datacenter"), Bool(risk, "is_tor"))
        { RiskScore = risk.ValueKind == JsonValueKind.Object && risk.TryGetProperty("risk_score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetInt32(out var n) && n is >= 0 and <= 100 ? n : null };
    }

    internal static IpIntelSource ParseIpLocate(string name, JsonElement r)
    {
        var asn = Obj(r, "asn");
        var company = Obj(r, "company");
        var privacy = Obj(r, "privacy");
        if (Str(r, "country_code") == null && privacy.ValueKind != JsonValueKind.Object) return Failed(name, "响应缺少位置和分类数据");
        return new IpIntelSource(name, true, null,
            Str(r, "country_code"), Str(r, "city"), Str(r, "subdivision"), Str(r, "time_zone"),
            Str(asn, "asn"), Str(company, "name") ?? Str(asn, "name"), Str(asn, "name"), Str(company, "type"),
            Bool(privacy, "is_proxy"), Bool(privacy, "is_vpn"), Bool(privacy, "is_hosting"), Bool(privacy, "is_tor"));
    }

    private static JsonElement Obj(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) ? v : default;

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
