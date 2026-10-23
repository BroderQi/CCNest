using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CCNest.Core.Infrastructure;

namespace CCNest.Core.Proxy;

public sealed record IpObservation(string Source, string? Ip, string? Country, string? Error, TimeSpan Latency)
{
    public bool Success => Ip != null;
}

public sealed record ExitIpResult(IReadOnlyList<IpObservation> Observations)
{
    /// <summary>The IP reported by the majority of successful sources.</summary>
    public string? ConsensusIp => Observations.Where(o => o.Success).GroupBy(o => o.Ip)
        .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    public bool AnySuccess => Observations.Any(o => o.Success);

    /// <summary>True when different sources saw different exit IPs (rotation or split routing).</summary>
    public bool Inconsistent => Observations.Where(o => o.Success).Select(o => o.Ip).Distinct().Count() > 1;

    public string? ClaudeEdgeCountry => Observations.FirstOrDefault(o => o.Source == IpEchoService.ClaudeTraceSource && o.Success)?.Country;

    public string Summary => AnySuccess
        ? string.Join("；", Observations.Select(o => o.Success ? $"{o.Source}={o.Ip}" : $"{o.Source} 失败"))
        : "所有出口 IP 检测服务均失败：" + string.Join("；", Observations.Select(o => $"{o.Source}: {o.Error}"));
}

/// <summary>
/// Determines the public exit IP of a route using several independent HTTPS services, including
/// Cloudflare's trace endpoint on claude.ai itself (which shows the IP and country that the
/// Claude edge actually sees).
/// </summary>
public static class IpEchoService
{
    public const string ClaudeTraceSource = "claude.ai 边缘";

    private sealed record Source(string Name, string Url, Func<string, (string? ip, string? country)> Parse);

    /// <summary>Test hook: replaces all echo sources with plain-text endpoints (name, url).</summary>
    internal static (string Name, string Url)[]? TestSources { get; set; }

    private static Source[] V4Sources => TestSources?.Select(t => new Source(t.Name, t.Url, ParsePlain)).ToArray() ?? DefaultSources;

    private static readonly Source[] DefaultSources =
    {
        new(ClaudeTraceSource, "https://claude.ai/cdn-cgi/trace", ParseTrace),
        new("Cloudflare", "https://www.cloudflare.com/cdn-cgi/trace", ParseTrace),
        new("ipify", "https://api.ipify.org?format=json", ParseIpJson),
        new("icanhazip", "https://ipv4.icanhazip.com", ParsePlain),
    };

    public static async Task<ExitIpResult> ObserveAsync(IReadOnlyList<ProxyEndpoint> hops, TimeSpan timeout,
        bool includeClaude = true, CancellationToken ct = default)
    {
        using var client = ChainHttp.Create(hops, timeout);
        var sources = includeClaude ? V4Sources : V4Sources.Where(s => s.Name != ClaudeTraceSource).ToArray();
        var tasks = sources.Select(s => QueryAsync(client, s, ct)).ToArray();
        return new ExitIpResult(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    /// <summary>Light-weight single check used by the periodic monitor (rotates through sources).</summary>
    public static async Task<IpObservation> QuickAsync(int localHttpProxyPort, int rotation, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = ChainHttp.ViaLocalProxy(localHttpProxyPort, timeout);
        var pool = V4Sources.Skip(1).ToArray(); // do not poll claude.ai in the background
        var first = pool[Math.Abs(rotation) % pool.Length];
        var result = await QueryAsync(client, first, ct).ConfigureAwait(false);
        if (result.Success) return result;
        var second = pool[(Math.Abs(rotation) + 1) % pool.Length];
        return await QueryAsync(client, second, ct).ConfigureAwait(false);
    }

    /// <summary>Checks whether a route has working IPv6 egress and returns the IPv6 address seen.</summary>
    public static async Task<IpObservation> ObserveIPv6Async(IReadOnlyList<ProxyEndpoint> hops, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = ChainHttp.Create(hops, timeout, hops.Count == 0 ? AddressFamily.InterNetworkV6 : AddressFamily.Unspecified);
        return await QueryAsync(client, new Source("ipify-v6", "https://api6.ipify.org?format=json", ParseIpJson), ct).ConfigureAwait(false);
    }

    private static async Task<IpObservation> QueryAsync(HttpClient client, Source source, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resp = await client.GetAsync(source.Url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new IpObservation(source.Name, null, null, $"HTTP {(int)resp.StatusCode}", sw.Elapsed);
            var (ip, country) = source.Parse(body);
            if (ip == null || !IPAddress.TryParse(ip, out _))
                return new IpObservation(source.Name, null, null, "响应中没有有效 IP", sw.Elapsed);
            return new IpObservation(source.Name, NormalizeIp(ip), country, null, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new IpObservation(source.Name, null, null, ChainHttp.Describe(ex), sw.Elapsed);
        }
    }

    public static string NormalizeIp(string ip) =>
        IPAddress.TryParse(ip.Trim(), out var parsed) ? (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString() : ip.Trim();

    public static bool SameIp(string? a, string? b) =>
        a != null && b != null && IPAddress.TryParse(a.Trim(), out var x) && IPAddress.TryParse(b.Trim(), out var y) &&
        (x.IsIPv4MappedToIPv6 ? x.MapToIPv4() : x).Equals(y.IsIPv4MappedToIPv6 ? y.MapToIPv4() : y);

    internal static (string?, string?) ParseTrace(string body)
    {
        string? ip = null, loc = null;
        foreach (var line in body.Split('\n'))
        {
            var kv = line.Trim().Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "ip") ip = kv[1];
            else if (kv[0] == "loc") loc = kv[1];
        }
        return (ip, loc);
    }

    internal static (string?, string?) ParseIpJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return (doc.RootElement.GetProperty("ip").GetString(), null);
        }
        catch (Exception ex)
        {
            Log.Debug("IpEcho", $"JSON 解析失败: {ex.Message}");
            return (null, null);
        }
    }

    internal static (string?, string?) ParsePlain(string body) => (body.Trim(), null);
}
