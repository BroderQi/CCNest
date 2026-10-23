using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCNest.Core.Infrastructure;

namespace CCNest.Core.Browser;

public sealed record BrowserCheckResult
{
    public List<string> Languages { get; init; } = new();
    public string? Language { get; init; }
    public string? TimeZone { get; init; }
    public string? Locale { get; init; }
    public int? TzOffsetMinutes { get; init; }
    public string? UserAgent { get; init; }
    public string? Brands { get; init; }
    public string? Platform { get; init; }
    public string? BrowserExitIp { get; init; }
    public List<string> WebrtcCandidates { get; init; } = new();
    public List<string> Errors { get; init; } = new();

    /// <summary>Public IPs revealed through WebRTC (server-reflexive candidates).</summary>
    public IReadOnlyList<string> WebRtcPublicIps => WebrtcCandidates
        .Select(c => Regex.Match(c, @"\s(\d{1,3}(?:\.\d{1,3}){3}|[0-9a-fA-F:]{6,})\s\d+\styp\s(srflx|prflx|relay)"))
        .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>Host candidates exposing a raw local IP (Chrome normally hides them behind mDNS .local names).</summary>
    public IReadOnlyList<string> WebRtcLocalIps => WebrtcCandidates
        .Select(c => Regex.Match(c, @"\s(\d{1,3}(?:\.\d{1,3}){3})\s\d+\styp\shost"))
        .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
}

/// <summary>
/// A tiny loopback-only HTTP server that serves the browser check page to the dedicated browser and
/// receives its results. It exposes no operations other than "serve page" and "accept result", requires
/// a random 128-bit path token, and rejects any Host header other than its own loopback address (DNS
/// rebinding protection). Web content can therefore never trigger privileged actions through it.
/// </summary>
public sealed class BrowserCheckServer : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly TaskCompletionSource<BrowserCheckResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public BrowserCheckServer()
    {
        _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/{_token}/";

    public async Task<BrowserCheckResult?> WaitAsync(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_result.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == _result.Task ? _result.Task.Result : null;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(Socket socket)
    {
        using var _ = socket;
        await using var stream = new NetworkStream(socket);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var (method, path, headers, body) = await ReadRequestAsync(stream, cts.Token).ConfigureAwait(false);
            if (!headers.TryGetValue("host", out var host) || host != $"127.0.0.1:{Port}")
            {
                await RespondAsync(stream, 421, "text/plain", "Misdirected Request").ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == $"/{_token}/")
            {
                await RespondAsync(stream, 200, "text/html; charset=utf-8", LoadPage().Replace("__TOKEN__", _token)).ConfigureAwait(false);
            }
            else if (method == "POST" && path == $"/{_token}/result")
            {
                BrowserCheckResult? result = null;
                try { result = JsonSerializer.Deserialize<BrowserCheckResult>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch (JsonException) { /* invalid */ }
                if (result == null)
                {
                    await RespondAsync(stream, 400, "text/plain", "bad request").ConfigureAwait(false);
                    return;
                }
                _result.TrySetResult(result);
                await RespondAsync(stream, 200, "application/json", "{\"ok\":true}").ConfigureAwait(false);
            }
            else
            {
                await RespondAsync(stream, 404, "text/plain", "not found").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("BrowserCheck", ex.Message);
        }
    }

    private static string LoadPage()
    {
        using var s = typeof(BrowserCheckServer).Assembly.GetManifestResourceStream("CCNest.BrowserCheck.html")!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new IOException("eof");
            buffer.Add(one[0]);
            var c = buffer.Count;
            if (c > 16 * 1024) throw new IOException("header too large");
            if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n' && buffer[c - 2] == '\r' && buffer[c - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) headers[line[..idx].Trim().ToLowerInvariant()] = line[(idx + 1)..].Trim();
        }
        var body = "";
        if (headers.TryGetValue("content-length", out var lenText) && int.TryParse(lenText, out var len) && len > 0)
        {
            if (len > 256 * 1024) throw new IOException("body too large");
            var data = new byte[len];
            await s.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(data);
        }
        return (first[0], first.Length > 1 ? first[1] : "/", headers, body);
    }

    private static async Task RespondAsync(Stream s, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 421 => "Misdirected Request", _ => "Error" };
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n" +
                     "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await s.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
    }
}
