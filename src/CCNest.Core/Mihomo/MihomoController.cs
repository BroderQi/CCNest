using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCNest.Core.Mihomo;

public sealed record MihomoVersion(string Version, bool Meta);

public sealed record MihomoProxyInfo(string Name, string Type, string? Now, IReadOnlyList<string> All, int? LastDelay);

public sealed record MihomoConnection(string Id, string Host, string DestinationIp, string DestinationPort, string Network,
    string Process, string ProcessPath, string Rule, string RulePayload, IReadOnlyList<string> Chains, DateTime Start);

public sealed record MihomoLogLine(string Type, string Payload);

public sealed class MihomoApiException : Exception
{
    public MihomoApiException(HttpStatusCode? status, string message) : base(message) => Status = status;
    public HttpStatusCode? Status { get; }
    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>
/// Client for the mihomo RESTful API. Works over TCP ("127.0.0.1:9097") and over Windows named pipes
/// ("\\.\pipe\verge-mihomo"), which is what current Clash Verge Rev versions use by default.
/// </summary>
public sealed class MihomoController : IDisposable
{
    private readonly HttpClient _http;

    public MihomoController(string address, string? secret, TimeSpan? timeout = null)
    {
        Address = address;
        var handler = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        Uri baseUri;
        if (IsPipeAddress(address))
        {
            var pipeName = PipeName(address);
            handler.ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ct).ConfigureAwait(false);
                return pipe;
            };
            baseUri = new Uri("http://localhost/");
        }
        else
        {
            var a = address.Trim();
            if (!a.Contains("://")) a = "http://" + a;
            var u = new Uri(a);
            var host = u.Host is "0.0.0.0" or "" ? "127.0.0.1" : u.Host == "[::]" ? "[::1]" : u.Host;
            baseUri = new Uri($"{u.Scheme}://{host}:{u.Port}/");
        }

        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public string Address { get; }

    public static bool IsPipeAddress(string address) =>
        address.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) || address.StartsWith("//./pipe/", StringComparison.OrdinalIgnoreCase);

    public static string PipeName(string address) => address.Replace('/', '\\')[@"\\.\pipe\".Length..];

    public async Task<MihomoVersion> GetVersionAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("version", ct).ConfigureAwait(false);
        return new MihomoVersion(json?["version"]?.GetValue<string>() ?? "unknown", json?["meta"]?.GetValue<bool>() ?? false);
    }

    public async Task<JsonObject> GetConfigsAsync(CancellationToken ct = default) =>
        (await GetJsonAsync("configs", ct).ConfigureAwait(false)) as JsonObject ?? new JsonObject();

    public async Task PatchConfigsAsync(JsonObject patch, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, "configs") { Content = JsonContent(patch) };
        await SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>Hot-reloads the full configuration from an in-memory payload (never touches disk).</summary>
    public async Task PutConfigPayloadAsync(string payload, bool force = true, CancellationToken ct = default)
    {
        var body = new JsonObject { ["path"] = "", ["payload"] = payload };
        using var req = new HttpRequestMessage(HttpMethod.Put, force ? "configs?force=true" : "configs") { Content = JsonContent(body) };
        await SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, MihomoProxyInfo>> GetProxiesAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("proxies", ct).ConfigureAwait(false);
        var result = new Dictionary<string, MihomoProxyInfo>(StringComparer.Ordinal);
        if (json?["proxies"] is JsonObject proxies)
        {
            foreach (var (name, node) in proxies)
            {
                if (node is not JsonObject p) continue;
                var all = p["all"] is JsonArray arr ? arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                int? delay = null;
                if (p["history"] is JsonArray h && h.Count > 0 && h[^1]?["delay"] is JsonNode d && d.GetValue<int>() > 0) delay = d.GetValue<int>();
                result[name] = new MihomoProxyInfo(name, p["type"]?.GetValue<string>() ?? "", p["now"]?.GetValue<string>(), all, delay);
            }
        }
        return result;
    }

    public async Task SelectProxyAsync(string group, string name, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"proxies/{Uri.EscapeDataString(group)}")
        {
            Content = JsonContent(new JsonObject { ["name"] = name }),
        };
        await SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>URL test through a proxy or group (follows dialer-proxy chains). Returns delay in ms.</summary>
    public async Task<(int? DelayMs, string? Error)> DelayAsync(string proxy, string url, int timeoutMs, CancellationToken ct = default)
    {
        var path = $"proxies/{Uri.EscapeDataString(proxy)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? json = null;
            try { json = JsonNode.Parse(text); } catch { /* not json */ }
            if (resp.IsSuccessStatusCode && json?["delay"] is JsonNode d) return (d.GetValue<int>(), null);
            return (null, json?["message"]?.GetValue<string>() ?? $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return (null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<MihomoConnection>> GetConnectionsAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("connections", ct).ConfigureAwait(false);
        var list = new List<MihomoConnection>();
        if (json?["connections"] is not JsonArray arr) return list;
        foreach (var c in arr.OfType<JsonObject>())
        {
            var md = c["metadata"] as JsonObject;
            string S(JsonNode? n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n?.ToString() ?? "";
            list.Add(new MihomoConnection(
                S(c["id"]), S(md?["host"]), S(md?["destinationIP"]), S(md?["destinationPort"]), S(md?["network"]),
                S(md?["process"]), S(md?["processPath"]), S(c["rule"]), S(c["rulePayload"]),
                c["chains"] is JsonArray ch ? ch.Select(x => S(x)).ToList() : new List<string>(),
                DateTime.TryParse(S(c["start"]), out var start) ? start : DateTime.MinValue));
        }
        return list;
    }

    public async Task CloseAllConnectionsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, "connections");
        await SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>Streams log lines (chunked JSON) until cancelled.</summary>
    public async IAsyncEnumerable<MihomoLogLine> StreamLogsAsync(string level, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"logs?level={level}");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = _http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        // Named-pipe streaming uses the primary client (its handler owns the pipe connect callback).
        var client = IsPipeAddress(Address) ? _http : http;
        if (!IsPipeAddress(Address)) http.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) yield break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node = null;
            try { node = JsonNode.Parse(line); } catch { /* ignore partial */ }
            if (node == null) continue;
            yield return new MihomoLogLine(node["type"]?.GetValue<string>() ?? "info", node["payload"]?.GetValue<string>() ?? "");
        }
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        var text = await SendAsync(req, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new MihomoApiException(null, $"无法连接控制接口 {Address}：{ex.Message}");
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return text;
            string? message = null;
            try { message = JsonNode.Parse(text)?["message"]?.GetValue<string>(); } catch { /* ignore */ }
            throw new MihomoApiException(resp.StatusCode, resp.StatusCode == HttpStatusCode.Unauthorized
                ? "控制接口密钥（secret）错误或缺失"
                : $"控制接口返回 {(int)resp.StatusCode}：{message ?? text}");
        }
    }

    private static StringContent JsonContent(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();
}
