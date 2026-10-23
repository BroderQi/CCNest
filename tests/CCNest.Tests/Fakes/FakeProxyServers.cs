using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CCNest.Tests.Fakes;

/// <summary>
/// Minimal in-process proxy servers used to build real multi-hop chains in tests.
/// Every CONNECT target is recorded; hostnames can be redirected to a local target so that
/// domain-based routing can be exercised without touching the internet.
/// </summary>
public abstract class FakeProxyBase : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public ConcurrentQueue<string> Targets { get; } = new();
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>When set, every outbound connection goes here regardless of the requested host.</summary>
    public IPEndPoint? RedirectAll { get; set; }

    /// <summary>When set, connections to hostnames (not IP literals) go here; IP literals are dialled normally.</summary>
    public IPEndPoint? RedirectHostnames { get; set; }

    /// <summary>When set, outbound connections are tunnelled through this upstream proxy (e.g. a local Clash port).</summary>
    public CCNest.Core.Proxy.ProxyEndpoint? Upstream { get; set; }

    public int Port { get; }
    public string Name { get; }
    public int AuthFailures;

    protected FakeProxyBase(string name)
    {
        Name = name;
        _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptSocketAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                using var s = new NetworkStream(client, true);
                try { await HandleAsync(s, _cts.Token); }
                catch { /* connection errors are expected in tests */ }
            });
        }
    }

    protected abstract Task HandleAsync(NetworkStream client, CancellationToken ct);

    protected async Task<Stream> ConnectOutAsync(string host, int port, CancellationToken ct)
    {
        Targets.Enqueue($"{host}:{port}");
        if (Upstream != null && RedirectAll == null && RedirectHostnames == null)
            return await CCNest.Core.Proxy.ProxyTunnel.ConnectAsync(new[] { Upstream }, host, port, TimeSpan.FromSeconds(15), ct);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (RedirectAll != null) await socket.ConnectAsync(RedirectAll, ct);
        else if (RedirectHostnames != null && !IPAddress.TryParse(host, out _)) await socket.ConnectAsync(RedirectHostnames, ct);
        else await socket.ConnectAsync(host, port, ct);
        return new NetworkStream(socket, true);
    }

    protected static async Task PipeAsync(Stream a, Stream b, CancellationToken ct)
    {
        var t1 = a.CopyToAsync(b, ct);
        var t2 = b.CopyToAsync(a, ct);
        await Task.WhenAny(t1, t2);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}

public sealed class FakeSocks5Proxy : FakeProxyBase
{
    public FakeSocks5Proxy(string name) : base(name) { }

    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var head = new byte[2];
        await s.ReadExactlyAsync(head, ct);
        var methods = new byte[head[1]];
        await s.ReadExactlyAsync(methods, ct);
        var needAuth = Username != null;
        if (needAuth)
        {
            if (!methods.Contains((byte)0x02)) { await s.WriteAsync(new byte[] { 5, 0xFF }, ct); return; }
            await s.WriteAsync(new byte[] { 5, 2 }, ct);
            var ver = new byte[2];
            await s.ReadExactlyAsync(ver, ct);
            var user = new byte[ver[1]];
            await s.ReadExactlyAsync(user, ct);
            var plen = new byte[1];
            await s.ReadExactlyAsync(plen, ct);
            var pass = new byte[plen[0]];
            await s.ReadExactlyAsync(pass, ct);
            if (Encoding.UTF8.GetString(user) != Username || Encoding.UTF8.GetString(pass) != Password)
            {
                Interlocked.Increment(ref AuthFailures);
                await s.WriteAsync(new byte[] { 1, 1 }, ct);
                return;
            }
            await s.WriteAsync(new byte[] { 1, 0 }, ct);
        }
        else
        {
            await s.WriteAsync(new byte[] { 5, 0 }, ct);
        }

        var req = new byte[4];
        await s.ReadExactlyAsync(req, ct);
        string host;
        switch (req[3])
        {
            case 1: { var b = new byte[4]; await s.ReadExactlyAsync(b, ct); host = new IPAddress(b).ToString(); break; }
            case 4: { var b = new byte[16]; await s.ReadExactlyAsync(b, ct); host = new IPAddress(b).ToString(); break; }
            default: { var l = new byte[1]; await s.ReadExactlyAsync(l, ct); var b = new byte[l[0]]; await s.ReadExactlyAsync(b, ct); host = Encoding.ASCII.GetString(b); break; }
        }
        var pb = new byte[2];
        await s.ReadExactlyAsync(pb, ct);
        var port = BinaryPrimitives.ReadUInt16BigEndian(pb);
        Stream o;
        try { o = await ConnectOutAsync(host, port, ct); }
        catch { await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, ct); return; }
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
        await using (o) await PipeAsync(s, o, ct);
    }
}

public sealed class FakeHttpProxy : FakeProxyBase
{
    public FakeHttpProxy(string name) : base(name) { }

    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var header = await ReadHeaderAsync(s, ct);
        var lines = header.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts[0] != "CONNECT") { await Write(s, "HTTP/1.1 405 Method Not Allowed\r\n\r\n", ct); return; }
        if (Username != null)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
            var auth = lines.FirstOrDefault(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
            if (auth == null || auth.Split(':', 2)[1].Trim() != expected)
            {
                Interlocked.Increment(ref AuthFailures);
                await Write(s, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"x\"\r\n\r\n", ct);
                return;
            }
        }
        var target = parts[1];
        var colon = target.LastIndexOf(':');
        var host = target[..colon].Trim('[', ']');
        var port = int.Parse(target[(colon + 1)..]);
        Stream o;
        try { o = await ConnectOutAsync(host, port, ct); }
        catch { await Write(s, "HTTP/1.1 502 Bad Gateway\r\n\r\n", ct); return; }
        await Write(s, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
        await using (o) await PipeAsync(s, o, ct);
    }

    private static Task Write(Stream s, string text, CancellationToken ct) => s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    private static async Task<string> ReadHeaderAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new IOException("eof");
            buf.Add(one[0]);
            var c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n')
                return Encoding.ASCII.GetString(buf.ToArray());
        }
    }
}

/// <summary>A plain HTTP server that answers every request with a fixed marker body.</summary>
public sealed class FakeHttpTarget : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public int Port { get; }
    public int Connections;
    public ConcurrentQueue<string> Hosts { get; } = new();

    /// <summary>Response body (e.g. an IP address when acting as an exit-IP echo service).</summary>
    public string Body { get; set; } = "ccnest-target-ok";

    public FakeHttpTarget()
    {
        _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(Loop);
    }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket c;
            try { c = await _listener.AcceptSocketAsync(_cts.Token); }
            catch { return; }
            Interlocked.Increment(ref Connections);
            _ = Task.Run(async () =>
            {
                using var s = new NetworkStream(c, true);
                try
                {
                    var reader = new StreamReader(s, Encoding.ASCII);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                        if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) Hosts.Enqueue(line[5..].Trim());
                    var body = Body;
                    var resp = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
                }
                catch { }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}
