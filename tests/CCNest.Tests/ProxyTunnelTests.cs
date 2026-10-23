using CCNest.Core.Proxy;
using CCNest.Tests.Fakes;

namespace CCNest.Tests;

public class ProxyTunnelTests
{
    private static ProxyEndpoint Ep(FakeProxyBase p, ProxyProtocol proto, string? user = null, string? pass = null) =>
        new() { Protocol = proto, Host = "127.0.0.1", Port = p.Port, Username = user, Password = pass };

    [Fact]
    public async Task TwoHopChain_ReachesTarget_InOrder()
    {
        await using var target = new FakeHttpTarget();
        await using var first = new FakeHttpProxy("first");
        await using var second = new FakeSocks5Proxy("second") { Username = "u-1", Password = "p:a@ss" };
        second.RedirectAll = target.EndPoint;

        var hops = new[] { Ep(first, ProxyProtocol.Http), Ep(second, ProxyProtocol.Socks5, "u-1", "p:a@ss") };
        using var client = ChainHttp.Create(hops, TimeSpan.FromSeconds(5));
        var body = await client.GetStringAsync("http://example.test/");

        Assert.Equal("ccnest-target-ok", body);
        Assert.Contains($"127.0.0.1:{second.Port}", first.Targets);
        Assert.Contains("example.test:80", second.Targets);
    }

    [Fact]
    public async Task WrongPasswordOnSecondHop_IsReportedAsAuthFailureOfHop1()
    {
        await using var first = new FakeSocks5Proxy("first");
        await using var second = new FakeHttpProxy("second") { Username = "user", Password = "right" };

        var hops = new[] { Ep(first, ProxyProtocol.Socks5), Ep(second, ProxyProtocol.Http, "user", "wrong") };
        var ex = await Assert.ThrowsAsync<ProxyTunnelException>(() =>
            ProxyTunnel.ConnectAsync(hops, "example.test", 443, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, ex.HopIndex);
        Assert.Equal(ProxyFailureKind.AuthFailed, ex.Kind);
    }

    [Fact]
    public async Task MissingCredentials_IsAuthRequired()
    {
        await using var proxy = new FakeSocks5Proxy("s") { Username = "a", Password = "b" };
        var ex = await Assert.ThrowsAsync<ProxyTunnelException>(() =>
            ProxyTunnel.ConnectAsync(new[] { Ep(proxy, ProxyProtocol.Socks5) }, "x.test", 80, TimeSpan.FromSeconds(5)));
        Assert.Equal(ProxyFailureKind.AuthRequired, ex.Kind);
    }

    [Fact]
    public async Task UnreachableFirstHop_IsConnectFailed()
    {
        var port = Core.SystemEnv.NetworkInspector.GetFreePort();
        var ex = await Assert.ThrowsAsync<ProxyTunnelException>(() =>
            ProxyTunnel.ConnectAsync(new[] { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = port } },
                "x.test", 80, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, ex.HopIndex);
        Assert.Equal(ProxyFailureKind.ConnectFailed, ex.Kind);
    }

    [Fact]
    public async Task WrongProtocol_SocksAgainstHttpProxy_IsProtocolError()
    {
        await using var http = new FakeHttpProxy("h");
        var ex = await Assert.ThrowsAnyAsync<ProxyTunnelException>(() =>
            ProxyTunnel.ConnectAsync(new[] { Ep(http, ProxyProtocol.Socks5) }, "x.test", 80, TimeSpan.FromSeconds(3)));
        Assert.Equal(0, ex.HopIndex);
    }
}
