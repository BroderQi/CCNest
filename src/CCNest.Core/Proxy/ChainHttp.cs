using System.Net;
using System.Net.Sockets;

namespace CCNest.Core.Proxy;

/// <summary>
/// Creates HttpClients whose TCP connections are tunnelled through a given proxy chain
/// (or go direct when the chain is empty). TLS, redirects and decompression are handled by
/// the standard .NET HTTP stack on top of the tunnel.
/// </summary>
public static class ChainHttp
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CCNest-Diagnostics";

    public static HttpClient Create(IReadOnlyList<ProxyEndpoint> hops, TimeSpan timeout,
        AddressFamily family = AddressFamily.Unspecified, bool allowRedirects = true)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = allowRedirects,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            ConnectTimeout = timeout,
            UseCookies = false,
            ConnectCallback = async (ctx, ct) =>
                await ProxyTunnel.ConnectAsync(hops, ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, timeout, ct, family).ConfigureAwait(false),
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout + TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>A client that talks through a single local HTTP proxy port (e.g. CCNest's mihomo).</summary>
    public static HttpClient ViaLocalProxy(int port, TimeSpan timeout) =>
        Create(new[] { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = port } }, timeout);

    /// <summary>Unwraps the innermost <see cref="ProxyTunnelException"/> from an HttpClient failure.</summary>
    public static ProxyTunnelException? FindTunnelError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is ProxyTunnelException pte) return pte;
        return null;
    }

    public static string Describe(Exception ex)
    {
        var tunnel = FindTunnelError(ex);
        if (tunnel != null) return tunnel.Message;
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException)
                return "TLS 握手失败（证书无效或连接被中间设备篡改）";
            if (e is TaskCanceledException or TimeoutException)
                return "请求超时";
        }
        return ex.InnerException?.Message ?? ex.Message;
    }
}
