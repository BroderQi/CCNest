using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CCNest.Core.Proxy;

public enum ProxyFailureKind
{
    ConnectFailed,
    Timeout,
    AuthRequired,
    AuthFailed,
    TargetUnreachable,
    TargetRefused,
    NotAllowed,
    ProtocolError,
}

public sealed class ProxyTunnelException : Exception
{
    public ProxyTunnelException(int hopIndex, ProxyFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        HopIndex = hopIndex;
        Kind = kind;
    }

    /// <summary>Index of the hop that failed (0 = first proxy). -1 for the final target via direct connection.</summary>
    public int HopIndex { get; }

    public ProxyFailureKind Kind { get; }
}

/// <summary>
/// Builds TCP tunnels through an arbitrary chain of HTTP CONNECT / SOCKS5 proxies. CCNest uses this
/// to test each stage of the route independently of mihomo, so failures can be attributed precisely
/// (first stage unreachable, second stage authentication failure, target unreachable, ...).
/// </summary>
public static class ProxyTunnel
{
    public static async Task<Stream> ConnectAsync(IReadOnlyList<ProxyEndpoint> hops, string targetHost, int targetPort,
        TimeSpan timeout, CancellationToken ct = default, AddressFamily family = AddressFamily.Unspecified)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        var firstHost = hops.Count > 0 ? hops[0].Host : targetHost;
        var firstPort = hops.Count > 0 ? hops[0].Port : targetPort;

        Socket? socket = null;
        try
        {
            socket = await ConnectSocketAsync(firstHost, firstPort, family, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket?.Dispose();
            throw new ProxyTunnelException(hops.Count > 0 ? 0 : -1, ProxyFailureKind.Timeout,
                hops.Count > 0 ? $"连接代理 {hops[0].Host}:{hops[0].Port} 超时" : $"连接 {targetHost}:{targetPort} 超时");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            socket?.Dispose();
            throw new ProxyTunnelException(hops.Count > 0 ? 0 : -1, ProxyFailureKind.ConnectFailed,
                hops.Count > 0 ? $"无法连接代理 {hops[0].Host}:{hops[0].Port}（{Describe(ex)}）" : $"无法连接 {targetHost}:{targetPort}（{Describe(ex)}）", ex);
        }

        Stream stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            for (var i = 0; i < hops.Count; i++)
            {
                var (nextHost, nextPort) = i + 1 < hops.Count ? (hops[i + 1].Host, hops[i + 1].Port) : (targetHost, targetPort);
                try
                {
                    await HandshakeAsync(stream, hops[i], nextHost, nextPort, i, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProxyTunnelException(i, ProxyFailureKind.Timeout, $"通过第 {i + 1} 级代理连接 {nextHost}:{nextPort} 超时");
                }
                catch (IOException ex)
                {
                    throw new ProxyTunnelException(i, ProxyFailureKind.ProtocolError, i > 0
                        ? $"与第 {i + 1} 级代理的连接在握手时被中断：上一级线路可能无法连通 {hops[i].Host}:{hops[i].Port}，或该服务器拒绝了连接"
                        : $"第 1 级代理在握手时断开连接（{ex.Message}）", ex);
                }
            }
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Socket> ConnectSocketAsync(string host, int port, AddressFamily family, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var ip)) addresses = new[] { ip };
        else addresses = await Dns.GetHostAddressesAsync(host, family, ct).ConfigureAwait(false);

        if (family != AddressFamily.Unspecified)
            addresses = addresses.Where(a => a.AddressFamily == family).ToArray();
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException)
            {
                last = ex;
                socket.Dispose();
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static Task HandshakeAsync(Stream stream, ProxyEndpoint hop, string host, int port, int hopIndex, CancellationToken ct) =>
        hop.Protocol switch
        {
            ProxyProtocol.Http => HttpConnectAsync(stream, hop, host, port, hopIndex, ct),
            ProxyProtocol.Socks5 => Socks5ConnectAsync(stream, hop, host, port, hopIndex, ct),
            _ => throw new NotSupportedException(),
        };

    internal static async Task HttpConnectAsync(Stream stream, ProxyEndpoint hop, string host, int port, int hopIndex, CancellationToken ct)
    {
        var authority = $"{ProxyEndpoint.FormatHost(host)}:{port}";
        var sb = new StringBuilder();
        sb.Append($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
        if (hop.HasCredentials)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{hop.Username}:{hop.Password}"));
            sb.Append($"Proxy-Authorization: Basic {token}\r\n");
        }
        sb.Append("Proxy-Connection: Keep-Alive\r\nUser-Agent: CCNest\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);

        var header = await ReadHttpHeaderAsync(stream, ct).ConfigureAwait(false);
        var statusLine = header.Split("\r\n", 2)[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[1], out var status))
            throw new ProxyTunnelException(hopIndex, ProxyFailureKind.ProtocolError, $"第 {hopIndex + 1} 级 HTTP 代理返回了无法识别的响应：{statusLine}");

        if (status is >= 200 and < 300) return;
        var reason = parts.Length > 2 ? parts[2] : "";
        throw status switch
        {
            407 => new ProxyTunnelException(hopIndex, hop.HasCredentials ? ProxyFailureKind.AuthFailed : ProxyFailureKind.AuthRequired,
                hop.HasCredentials ? $"第 {hopIndex + 1} 级代理认证失败（407）：用户名或密码错误，或该 IP 未授权" : $"第 {hopIndex + 1} 级代理需要用户名和密码（407）"),
            403 => new ProxyTunnelException(hopIndex, ProxyFailureKind.NotAllowed, $"第 {hopIndex + 1} 级代理拒绝访问 {authority}（403 {reason}）"),
            502 or 503 or 504 => new ProxyTunnelException(hopIndex, ProxyFailureKind.TargetUnreachable, $"第 {hopIndex + 1} 级代理无法连接 {authority}（{status} {reason}）"),
            _ => new ProxyTunnelException(hopIndex, ProxyFailureKind.ProtocolError, $"第 {hopIndex + 1} 级代理返回 {status} {reason}"),
        };
    }

    private static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken ct)
    {
        // Read byte-by-byte so no tunnel payload is consumed past the header terminator.
        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            var n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("代理在返回响应前关闭了连接");
            buffer.Add(one[0]);
            var c = buffer.Count;
            if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n' && buffer[c - 2] == '\r' && buffer[c - 1] == '\n')
                return Encoding.ASCII.GetString(buffer.ToArray());
        }
        throw new IOException("代理响应头过长");
    }

    internal static async Task Socks5ConnectAsync(Stream stream, ProxyEndpoint hop, string host, int port, int hopIndex, CancellationToken ct)
    {
        var greeting = hop.HasCredentials ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        var choice = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
        if (choice[0] != 0x05)
            throw new ProxyTunnelException(hopIndex, ProxyFailureKind.ProtocolError, $"第 {hopIndex + 1} 级代理不是 SOCKS5 服务器（可能应选择 HTTP 协议）");

        switch (choice[1])
        {
            case 0x00:
                break;
            case 0x02:
                if (!hop.HasCredentials)
                    throw new ProxyTunnelException(hopIndex, ProxyFailureKind.AuthRequired, $"第 {hopIndex + 1} 级 SOCKS5 代理需要用户名和密码");
                var user = Encoding.UTF8.GetBytes(hop.Username ?? "");
                var pass = Encoding.UTF8.GetBytes(hop.Password ?? "");
                if (user.Length > 255 || pass.Length > 255)
                    throw new ProxyTunnelException(hopIndex, ProxyFailureKind.ProtocolError, "SOCKS5 用户名或密码超过 255 字节");
                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 0x01;
                auth[1] = (byte)user.Length;
                user.CopyTo(auth, 2);
                auth[2 + user.Length] = (byte)pass.Length;
                pass.CopyTo(auth, 3 + user.Length);
                await stream.WriteAsync(auth, ct).ConfigureAwait(false);
                var authReply = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
                if (authReply[1] != 0x00)
                    throw new ProxyTunnelException(hopIndex, ProxyFailureKind.AuthFailed, $"第 {hopIndex + 1} 级 SOCKS5 代理认证失败：用户名或密码错误");
                break;
            case 0xFF:
                throw new ProxyTunnelException(hopIndex, hop.HasCredentials ? ProxyFailureKind.ProtocolError : ProxyFailureKind.AuthRequired,
                    hop.HasCredentials ? $"第 {hopIndex + 1} 级 SOCKS5 代理不接受用户名密码认证方式" : $"第 {hopIndex + 1} 级 SOCKS5 代理需要认证");
            default:
                throw new ProxyTunnelException(hopIndex, ProxyFailureKind.ProtocolError, $"第 {hopIndex + 1} 级 SOCKS5 代理选择了不支持的认证方式 0x{choice[1]:X2}");
        }

        var request = new List<byte> { 0x05, 0x01, 0x00 };
        if (IPAddress.TryParse(host, out var ip))
        {
            request.Add(ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x04 : (byte)0x01);
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255) throw new ProxyTunnelException(hopIndex, ProxyFailureKind.ProtocolError, "目标域名过长");
            request.Add(0x03);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }
        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        request.AddRange(portBytes);
        await stream.WriteAsync(request.ToArray(), ct).ConfigureAwait(false);

        var head = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (head[1] != 0x00)
        {
            var target = $"{host}:{port}";
            throw head[1] switch
            {
                0x02 => new ProxyTunnelException(hopIndex, ProxyFailureKind.NotAllowed, $"第 {hopIndex + 1} 级代理规则禁止访问 {target}"),
                0x03 or 0x04 => new ProxyTunnelException(hopIndex, ProxyFailureKind.TargetUnreachable, $"第 {hopIndex + 1} 级代理无法到达 {target}（网络/主机不可达）"),
                0x05 => new ProxyTunnelException(hopIndex, ProxyFailureKind.TargetRefused, $"{target} 拒绝了第 {hopIndex + 1} 级代理的连接"),
                0x06 => new ProxyTunnelException(hopIndex, ProxyFailureKind.Timeout, $"第 {hopIndex + 1} 级代理连接 {target} 超时（TTL expired）"),
                _ => new ProxyTunnelException(hopIndex, ProxyFailureKind.TargetUnreachable, $"第 {hopIndex + 1} 级代理连接 {target} 失败（SOCKS5 错误码 {head[1]}）"),
            };
        }

        var skip = head[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadExactAsync(stream, 1, ct).ConfigureAwait(false))[0],
            _ => throw new ProxyTunnelException(hopIndex, ProxyFailureKind.ProtocolError, "SOCKS5 响应地址类型无效"),
        };
        await ReadExactAsync(stream, skip + 2, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that a proxy server actually answers on an established tunnel, without sending credentials.
    /// SOCKS5: a method-selection greeting must be answered with version 5. HTTP: any HTTP status line.
    /// Returns null when the server responds, otherwise a Chinese description of what went wrong.
    /// </summary>
    public static async Task<string?> ProbeServerAsync(Stream stream, ProxyProtocol protocol, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            if (protocol == ProxyProtocol.Socks5)
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 }, cts.Token).ConfigureAwait(false);
                var reply = new byte[2];
                var read = 0;
                while (read < 2)
                {
                    var n = await stream.ReadAsync(reply.AsMemory(read), cts.Token).ConfigureAwait(false);
                    if (n == 0) return "连接在服务器响应前被关闭：上一级线路未能连通该服务器，或服务器拒绝了连接";
                    read += n;
                }
                return reply[0] == 0x05 ? null : "该端口不是 SOCKS5 代理（协议类型可能应选择 HTTP）";
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT ccnest.invalid:443 HTTP/1.1\r\nHost: ccnest.invalid:443\r\n\r\n"), cts.Token).ConfigureAwait(false);
            var buffer = new byte[12];
            var got = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (got == 0) return "连接在服务器响应前被关闭：上一级线路未能连通该服务器，或服务器拒绝了连接";
            return Encoding.ASCII.GetString(buffer, 0, got).StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
                ? null
                : "该端口不是 HTTP 代理（协议类型可能应选择 SOCKS5）";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "等待代理服务器响应超时：上一级线路可能无法连通该服务器";
        }
        catch (IOException)
        {
            return "连接在服务器响应前被关闭：上一级线路未能连通该服务器，或服务器拒绝了连接";
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    private static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "连接被拒绝，端口未在监听",
        SocketException { SocketErrorCode: SocketError.HostNotFound } => "域名无法解析",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "超时",
        SocketException { SocketErrorCode: SocketError.NetworkUnreachable } => "网络不可达",
        SocketException { SocketErrorCode: SocketError.HostUnreachable } => "主机不可达",
        SocketException { SocketErrorCode: SocketError.AccessDenied } => "被系统或防火墙阻止",
        SocketException s => s.SocketErrorCode.ToString(),
        _ => ex.Message,
    };
}
