using CCNest.Core.Infrastructure;
using CCNest.Core.Proxy;

namespace CCNest.Tests;

public class ProxyEndpointTests
{
    [Theory]
    [InlineData("socks5://user:pass@1.2.3.4:1080", ProxyProtocol.Socks5, "1.2.3.4", 1080, "user", "pass")]
    [InlineData("http://u%40x:p%3Aw@proxy.example.com:8080", ProxyProtocol.Http, "proxy.example.com", 8080, "u@x", "p:w")]
    [InlineData("geo.iproyal.com:12321:myuser:mypass_country-us", ProxyProtocol.Socks5, "geo.iproyal.com", 12321, "myuser", "mypass_country-us")]
    [InlineData("1.2.3.4:8000:user:pa:ss", ProxyProtocol.Http, "1.2.3.4", 8000, "user", "pa:ss")]
    [InlineData("user-abc:secret@isp.oxylabs.io:8001", ProxyProtocol.Http, "isp.oxylabs.io", 8001, "user-abc", "secret")]
    [InlineData("10.0.0.1:3128", ProxyProtocol.Http, "10.0.0.1", 3128, null, null)]
    [InlineData("user:pass:5.6.7.8:7777", ProxyProtocol.Socks5, "5.6.7.8", 7777, "user", "pass")]
    [InlineData("socks5h://[2001:db8::1]:1080", ProxyProtocol.Socks5, "2001:db8::1", 1080, null, null)]
    public void Parse_CommonProviderFormats(string input, ProxyProtocol defaultProto, string host, int port, string? user, string? pass)
    {
        Assert.True(ProxyEndpoint.TryParse(input, defaultProto, out var ep, out var err), err);
        Assert.Equal(host, ep.Host);
        Assert.Equal(port, ep.Port);
        Assert.Equal(user, ep.Username);
        Assert.Equal(pass, ep.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://1.2.3.4:21")]
    [InlineData("hostonly")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("bad host:80")]
    public void Parse_RejectsInvalid(string input)
    {
        Assert.False(ProxyEndpoint.TryParse(input, ProxyProtocol.Http, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void ToString_NeverContainsPassword()
    {
        var ep = new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "h.example", Port = 1, Username = "u", Password = "topsecret" };
        Assert.DoesNotContain("topsecret", ep.ToString());
    }

    [Fact]
    public void Redactor_MasksRegisteredSecretsAndPatterns()
    {
        SecretRedactor.Register("Sup3r-Secret");
        var text = SecretRedactor.Redact("password: Sup3r-Secret, url socks5://bob:hunter2@1.2.3.4:1080 Proxy-Authorization: Basic Ym9iOmh1bnRlcjI=");
        Assert.DoesNotContain("Sup3r-Secret", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("Ym9iOmh1bnRlcjI=", text);
        Assert.Contains("bob", text);
    }
}

public class EndpointHeuristicsTests
{
    [Theory]
    [InlineData("user123-region-US-st-Ohio-sid-AbCdEf12-t-120", true, true, 120)]
    [InlineData("customer-abc-session-xyz789", true, false, null)]
    [InlineData("user-country-us", false, true, null)]
    public void DetectsStickyAndGeoPatterns(string user, bool sticky, bool geo, int? minutes)
    {
        var f = EndpointHeuristics.Analyze(new ProxyEndpoint { Host = "h.example", Port = 1, Username = user });
        Assert.NotNull(f);
        Assert.Equal(sticky, f!.LooksSticky);
        Assert.Equal(geo, f.LooksGeoTargetedPool);
        Assert.Equal(minutes, f.SessionMinutes);
        Assert.StartsWith("（推测）", f.Message);
    }

    [Theory]
    [InlineData("plainuser")]
    [InlineData("john.smith")]
    [InlineData(null)]
    public void PlainStaticCredentials_ProduceNoFinding(string? user) =>
        Assert.Null(EndpointHeuristics.Analyze(new ProxyEndpoint { Host = "h.example", Port = 1, Username = user }));
}
