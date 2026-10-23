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
    string? Timezone, string? Asn, string? Org, string? Isp, string? Type, bool? IsProxy, bool? IsVpn, bool? IsDatacenter, bool? IsTor);

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

    public IpClass Classification
    {
        get
        {
            var ipapi = Ok.FirstOrDefault(s => s.Name == "ipapi.is");
            if (ipapi == null) return IpClass.Unknown;
            if (ipapi.IsDatacenter == true) return IpClass.Datacenter;
            return ipapi.Type?.ToLowerInvariant() switch
            {
                "hosting" => IpClass.Datacenter,
                "isp" => IpClass.ResidentialOrIsp,
                "business" => IpClass.Business,
                "education" => IpClass.Education,
                "government" => IpClass.Government,
                "mobile" => IpClass.Mobile,
                _ => IpClass.Unknown,
            };
        }
    }

    public bool AnyProxyFlag => Ok.Any(s => s.IsProxy == true || s.IsVpn == true || s.IsTor == true);

    public static string Describe(IpClass c) => c switch
    {
        IpClass.ResidentialOrIsp => "住宅 / ISP 网络",
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
    public static async Task<IpIntel> LookupAsync(string ip, IReadOnlyList<ProxyEndpoint> route, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = ChainHttp.Create(route, timeout);
        var tasks = new[]
        {
            Query(client, "ipapi.is", $"https://api.ipapi.is/?q={Uri.EscapeDataString(ip)}", ParseIpapiIs, ct),
            Query(client, "ipwho.is", $"https://ipwho.is/{Uri.EscapeDataString(ip)}", ParseIpwhoIs, ct),
            Query(client, "ipinfo.io", $"https://ipinfo.io/{Uri.EscapeDataString(ip)}/json", ParseIpinfo, ct),
        };
        return new IpIntel(ip, await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<IpIntelSource> Query(HttpClient client, string name, string url, Func<string, JsonElement, IpIntelSource> parse, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Failed(name, $"HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(body);
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

    internal static IpIntelSource ParseIpapiIs(string name, JsonElement r)
    {
        var loc = r.TryGetProperty("location", out var l) ? l : default;
        var company = r.TryGetProperty("company", out var c) ? c : default;
        var asn = r.TryGetProperty("asn", out var a) ? a : default;
        return new IpIntelSource(name, true, null,
            Str(loc, "country_code"), Str(loc, "city"), Str(loc, "state"), Str(loc, "timezone"),
            asn.ValueKind == JsonValueKind.Object && asn.TryGetProperty("asn", out var n) ? "AS" + n.ToString() : null,
            Str(asn, "org"), Str(company, "name"), Str(company, "type"),
            Bool(r, "is_proxy"), Bool(r, "is_vpn"), Bool(r, "is_datacenter"), Bool(r, "is_tor"));
    }

    internal static IpIntelSource ParseIpwhoIs(string name, JsonElement r)
    {
        if (Bool(r, "success") == false) return Failed(name, Str(r, "message") ?? "查询失败");
        var conn = r.TryGetProperty("connection", out var c) ? c : default;
        var tz = r.TryGetProperty("timezone", out var t) ? t : default;
        return new IpIntelSource(name, true, null,
            Str(r, "country_code"), Str(r, "city"), Str(r, "region"), Str(tz, "id"),
            conn.ValueKind == JsonValueKind.Object && conn.TryGetProperty("asn", out var n) ? "AS" + n.ToString() : null,
            Str(conn, "org"), Str(conn, "isp"), null, null, null, null, null);
    }

    internal static IpIntelSource ParseIpinfo(string name, JsonElement r)
    {
        if (r.TryGetProperty("bogon", out var b) && b.ValueKind == JsonValueKind.True) return Failed(name, "保留地址");
        var org = Str(r, "org");
        string? asn = null, orgName = org;
        if (org != null && org.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            var space = org.IndexOf(' ');
            asn = space > 0 ? org[..space] : org;
            orgName = space > 0 ? org[(space + 1)..] : null;
        }
        return new IpIntelSource(name, true, null, Str(r, "country"), Str(r, "city"), Str(r, "region"), Str(r, "timezone"),
            asn, orgName, null, null, null, null, null, null);
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
