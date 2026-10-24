using System.Net.Sockets;
using System.Text;
using CCNest.Core.Browser;
using CCNest.Core.Diagnostics;
using CCNest.Core.Proxy;
using CCNest.Core.SystemEnv;

namespace CCNest.Tests;

public class MiscTests
{
    [Fact]
    public void CloudflareTrace_IsParsed()
    {
        var (ip, loc) = IpEchoService.ParseTrace("fl=1\nh=claude.ai\nip=203.0.113.9\nts=1\nloc=US\nwarp=off\n");
        Assert.Equal("203.0.113.9", ip);
        Assert.Equal("US", loc);
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", true)]
    [InlineData("::ffff:1.2.3.4", "1.2.3.4", true)]
    [InlineData("1.2.3.4", "1.2.3.5", false)]
    [InlineData(null, "1.2.3.4", false)]
    public void SameIp(string? a, string? b, bool expected) => Assert.Equal(expected, IpEchoService.SameIp(a, b));

    [Theory]
    [InlineData("127.0.0.1:7897", 7897)]
    [InlineData("http=127.0.0.1:7899;https=127.0.0.1:7899", 7899)]
    [InlineData("proxy.corp:8080", null)]
    public void SystemProxyLoopbackPort(string server, int? port) =>
        Assert.Equal(port, new SystemProxyState { ProxyEnabled = true, ProxyServer = server }.LoopbackPort());

    [Theory]
    [InlineData("http://127.0.0.1:17897", 17897)]
    [InlineData("127.0.0.1:7890", 7890)]
    [InlineData("http://proxy.example:3128", null)]
    public void EnvLoopbackPort(string value, int? port) => Assert.Equal(port, DiagnosticsEngine.LoopbackPortOf(value));

    [Fact]
    public void WebRtcCandidates_AreClassified()
    {
        var r = new BrowserCheckResult
        {
            WebrtcCandidates = new()
            {
                "candidate:1 1 udp 2122260223 192.168.1.20 54321 typ host generation 0",
                "candidate:2 1 udp 2122260223 abcd1234.local 54322 typ host generation 0",
                "candidate:3 1 udp 1686052607 198.51.100.4 54321 typ srflx raddr 192.168.1.20 rport 54321",
            },
        };
        Assert.Equal(new[] { "198.51.100.4" }, r.WebRtcPublicIps);
        Assert.Equal(new[] { "192.168.1.20" }, r.WebRtcLocalIps);
    }

    [Fact]
    public async Task BrowserCheckServer_ServesPage_RejectsForeignHost_AcceptsResult()
    {
        await using var server = new BrowserCheckServer();
        var url = new Uri(server.Url);

        using (var http = new HttpClient())
        {
            var page = await http.GetStringAsync(server.Url);
            Assert.Contains(url.AbsolutePath.Trim('/'), page); // token embedded
            Assert.DoesNotContain("__TOKEN__", page);
        }

        // DNS-rebinding style request with a foreign Host header.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", server.Port);
            var s = tcp.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes($"GET {url.AbsolutePath} HTTP/1.1\r\nHost: evil.example:{server.Port}\r\n\r\n"));
            var buf = new byte[64];
            var n = await s.ReadAsync(buf);
            Assert.Contains("421", Encoding.ASCII.GetString(buf, 0, n));
        }

        using (var http = new HttpClient())
        {
            var wrongToken = await http.PostAsync($"http://127.0.0.1:{server.Port}/deadbeef/result", new StringContent("{}"));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, wrongToken.StatusCode);
            var ok = await http.PostAsync(server.Url + "result", new StringContent("""{"languages":["en-US","en"],"timeZone":"America/Los_Angeles","browserExitIp":"203.0.113.1"}""", Encoding.UTF8, "application/json"));
            Assert.True(ok.IsSuccessStatusCode);
        }

        var result = await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.Equal("America/Los_Angeles", result!.TimeZone);
        Assert.Equal("en-US", result.Languages[0]);
    }

    [Fact]
    public void DedicatedBrowser_Arguments_ForceProxyAndIsolatedProfile()
    {
        var browser = new BrowserInstall(Core.Settings.BrowserKind.Chrome, "Chrome", @"C:\chrome.exe", "1");
        var args = DedicatedBrowser.BuildArguments(browser, new Core.Settings.BrowserSettings(), 17897, "https://claude.ai/");
        Assert.Contains("--proxy-server=http://127.0.0.1:17897", args);
        Assert.Contains(args, a => a.StartsWith("--user-data-dir=") && a.Contains(@"\browser\chrome\UserData"));
        Assert.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", args);
        Assert.Equal("https://claude.ai/", args[^1]);
    }

    [Fact]
    public void ClaudeDomains_AreNormalized()
    {
        var all = Core.Mihomo.ClaudeDomains.All(new[] { ".Example.COM ", "claude.ai" }).ToList();
        Assert.Contains("example.com", all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Contains("ipquery.io", all);
        Assert.Contains("iplocate.io", all);
        Assert.DoesNotContain("ipapi.is", all);
    }
}
