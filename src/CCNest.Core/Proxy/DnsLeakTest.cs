using System.Text.Json;

namespace CCNest.Core.Proxy;

public sealed record DnsResolverObservation(string Ip, string? Country, string? Asn);

public sealed record DnsLeakResult(bool Available, string? Error, IReadOnlyList<DnsResolverObservation> Resolvers, string? ExitIpSeen);

/// <summary>
/// DNS leak test using the public bash.ws service: unique random hostnames are requested through the
/// route under test, so the authoritative server learns which recursive resolvers looked them up.
/// Through a correct chain the resolvers belong to the second-stage provider's network, never to the
/// local ISP. The service is third-party and may be unavailable; the result then reads "unknown".
/// </summary>
public static class DnsLeakTest
{
    public static async Task<DnsLeakResult> RunAsync(IReadOnlyList<ProxyEndpoint> route, CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(10);
        string id;
        try
        {
            using var client = ChainHttp.Create(route, timeout);
            id = (await client.GetStringAsync("https://bash.ws/id", ct).ConfigureAwait(false)).Trim();
            if (id.Length is < 8 or > 64 || !id.All(char.IsLetterOrDigit)) return new DnsLeakResult(false, "检测服务返回了无效的标识", Array.Empty<DnsResolverObservation>(), null);
        }
        catch (Exception ex)
        {
            return new DnsLeakResult(false, "DNS 泄漏检测服务不可用：" + ChainHttp.Describe(ex), Array.Empty<DnsResolverObservation>(), null);
        }

        // Each connection attempt makes the far end of the route resolve a unique name.
        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            try
            {
                await using var _ = await ProxyTunnel.ConnectAsync(route, $"{i}.{id}.bash.ws", 80, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            }
            catch { /* expected: the names do not resolve to a server */ }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        try
        {
            using var client = ChainHttp.Create(route, timeout);
            var json = await client.GetStringAsync($"https://bash.ws/dnsleak/test/{id}?json", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var resolvers = new List<DnsResolverObservation>();
            string? exitIp = null;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                var ip = e.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() : null;
                var country = e.TryGetProperty("country_name", out var c) ? c.GetString() : null;
                var asn = e.TryGetProperty("asn", out var a) ? a.GetString() : null;
                if (type == "ip") exitIp = ip;
                else if (type == "dns" && ip != null) resolvers.Add(new DnsResolverObservation(ip, country, asn));
            }
            return new DnsLeakResult(true, null, resolvers, exitIp);
        }
        catch (Exception ex)
        {
            return new DnsLeakResult(false, "无法获取 DNS 检测结果：" + ChainHttp.Describe(ex), Array.Empty<DnsResolverObservation>(), null);
        }
    }
}
