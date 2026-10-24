using System.Diagnostics;

namespace CCNest.Core.Proxy;

public enum StepStatus
{
    Pending,
    Running,
    Passed,
    Warning,
    Failed,
    Skipped,
}

public sealed record ChainStep(string Id, string Title, StepStatus Status, string Detail, TimeSpan Duration, string? Hint = null);

public enum RouteTestMode
{
    Original,
    Protected,
}

public sealed record ChainTestResult(IReadOnlyList<ChainStep> Steps, string? FirstStageExitIp, string? ExitIp, string? ExitCountry,
    string? ClaudeEdgeIp, string? ClaudeEdgeCountry, IpIntel? Intel)
{
    public RouteTestMode Mode { get; init; } = RouteTestMode.Original;
    public bool Passed => Steps.All(s => s.Status is StepStatus.Passed or StepStatus.Warning or StepStatus.Skipped);
    public ChainStep? FirstFailure => Steps.FirstOrDefault(s => s.Status == StepStatus.Failed);
}

/// <summary>
/// Stage-by-stage verification of the configured route, independent of mihomo:
///   1. local machine → first stage reachable
///   2. first stage exit IP
///   3. first stage → second stage server reachable (TCP through stage 1)
///   4. second stage authentication + exit IP through the full chain
///   5. comparison with the expected static IP
///   6. Claude endpoints reachable through the chain (claude.ai edge view, api.anthropic.com)
/// Each failure is attributed to the exact stage with a Chinese explanation and a hint.
/// </summary>
public static class ChainTester
{
    /// <summary>Endpoints whose reachability proves Claude works through the route (test hook: settable).</summary>
    internal static string[] ClaudeProbeUrls { get; set; } = { "https://claude.ai/cdn-cgi/trace", "https://api.anthropic.com/" };

    public static async Task<ChainTestResult> RunAsync(IReadOnlyList<ProxyEndpoint> firstStage, ProxyEndpoint secondStage, string? expectedIp,
        IProgress<ChainStep>? progress = null, bool lookupIntel = true, CancellationToken ct = default)
    {
        var steps = new List<ChainStep>();
        var timeout = TimeSpan.FromSeconds(12);
        string? firstExit = null, exit = null, exitCountry = null, edgeIp = null, edgeCountry = null;
        IpIntel? intel = null;

        void Add(ChainStep s)
        {
            steps.Add(s);
            progress?.Report(s);
        }

        var full = firstStage.Concat(new[] { secondStage }).ToList();

        // 1 + 2: first stage
        if (firstStage.Count == 0)
        {
            Add(new ChainStep("first", "一级线路", StepStatus.Skipped, "未使用一级代理，CCNest 将直接连接二级代理", TimeSpan.Zero));
        }
        else
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var obs = await IpEchoService.ObserveAsync(firstStage, timeout, includeClaude: false, ct).ConfigureAwait(false);
                if (!obs.AnySuccess)
                {
                    var err = await ProbeTunnelError(firstStage, timeout, ct).ConfigureAwait(false);
                    if (err != null) throw new InvalidOperationException(err);
                }
                firstExit = obs.ConsensusIp;
                if (firstExit == null)
                {
                    // The local port answers, but nothing gets through it: the current node is down.
                    Add(new ChainStep("first", "一级线路", StepStatus.Failed,
                        "可以连接一级代理端口，但无法通过它访问互联网（当前节点可能不可用或已断开）", sw.Elapsed,
                        "请在 Clash / Mihomo 中切换到可用节点并测速，或改用其他一级线路。"));
                    return new ChainTestResult(steps, null, null, null, null, null, null);
                }
                Add(new ChainStep("first", "一级线路", StepStatus.Passed, $"一级代理可用，一级出口 IP：{firstExit}", sw.Elapsed));
            }
            catch (Exception ex)
            {
                Add(new ChainStep("first", "一级线路", StepStatus.Failed, ChainHttp.Describe(ex), sw.Elapsed,
                    "请确认 Clash/Mihomo 正在运行、本地端口正确，并且当前节点可用。"));
                return new ChainTestResult(steps, null, null, null, null, null, null);
            }
        }

        // 3: reach the second stage server through the first stage. Some first stages (mihomo among them)
        // acknowledge CONNECT before dialling, so a successful tunnel alone proves nothing: the server must
        // also answer its own protocol greeting.
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await using var s = await ProxyTunnel.ConnectAsync(firstStage, secondStage.Host, secondStage.Port, timeout, ct).ConfigureAwait(false);
                var greeting = await ProxyTunnel.ProbeServerAsync(s, secondStage.Protocol, timeout, ct).ConfigureAwait(false);
                if (greeting != null)
                {
                    Add(new ChainStep("reach", "连接二级代理服务器", StepStatus.Failed, greeting, sw.Elapsed, ReachHint(firstStage.Count > 0)));
                    return new ChainTestResult(steps, firstExit, null, null, null, null, null);
                }
                Add(new ChainStep("reach", "连接二级代理服务器", StepStatus.Passed,
                    $"{(firstStage.Count > 0 ? "经一级线路" : "直接")}连通 {ProxyEndpoint.FormatHost(secondStage.Host)}:{secondStage.Port}，服务器响应正常（{sw.ElapsedMilliseconds} ms）", sw.Elapsed));
            }
            catch (Exception ex)
            {
                Add(new ChainStep("reach", "连接二级代理服务器", StepStatus.Failed, ChainHttp.Describe(ex), sw.Elapsed, ReachHint(firstStage.Count > 0)));
                return new ChainTestResult(steps, firstExit, null, null, null, null, null);
            }
        }

        // 4: authenticate + exit IP through the whole chain
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var obs = await IpEchoService.ObserveAsync(full, timeout, includeClaude: true, ct).ConfigureAwait(false);
                if (!obs.AnySuccess)
                {
                    var tunnelErr = await ProbeTunnelError(full, timeout, ct).ConfigureAwait(false);
                    Add(new ChainStep("auth", "二级代理认证与出口", StepStatus.Failed, tunnelErr ?? obs.Summary, sw.Elapsed,
                        tunnelErr?.Contains("认证") == true ? "请核对服务商后台的用户名、密码，以及是否需要把一级出口 IP 加入白名单。" : null));
                    return new ChainTestResult(steps, firstExit, null, null, null, null, null);
                }
                exit = obs.ConsensusIp;
                var edge = obs.Observations.FirstOrDefault(o => o.Source == IpEchoService.ClaudeTraceSource);
                edgeIp = edge?.Ip;
                edgeCountry = edge?.Country;
                exitCountry = obs.Observations.FirstOrDefault(o => o.Country != null)?.Country;
                var status = obs.Inconsistent ? StepStatus.Warning : StepStatus.Passed;
                var detail = obs.Inconsistent
                    ? $"不同检测源看到的出口 IP 不一致（{obs.Summary}），二级代理可能是轮换 IP 或存在分流"
                    : $"认证成功，最终出口 IP：{exit}";
                Add(new ChainStep("auth", "二级代理认证与出口", status, detail, sw.Elapsed,
                    obs.Inconsistent ? "静态住宅/ISP 代理应始终返回同一个 IP；请在服务商后台确认使用的是静态（Sticky/Static）端点。" : null));
                if (firstExit != null && IpEchoService.SameIp(firstExit, exit))
                {
                    Add(new ChainStep("distinct", "出口独立性", StepStatus.Failed, "最终出口与一级出口相同，二级代理没有生效", TimeSpan.Zero,
                        "请检查二级代理信息是否填写为服务商提供的代理地址。"));
                }
            }
            catch (Exception ex)
            {
                Add(new ChainStep("auth", "二级代理认证与出口", StepStatus.Failed, ChainHttp.Describe(ex), sw.Elapsed));
                return new ChainTestResult(steps, firstExit, null, null, null, null, null);
            }
        }

        // 5: expected IP
        if (string.IsNullOrWhiteSpace(expectedIp))
        {
            Add(new ChainStep("expected", "期望出口 IP", StepStatus.Warning, $"尚未设置期望出口 IP。当前出口为 {exit}，确认无误后可一键设为期望 IP。", TimeSpan.Zero));
        }
        else if (IpEchoService.SameIp(expectedIp, exit) && (edgeIp == null || IpEchoService.SameIp(expectedIp, edgeIp)))
        {
            Add(new ChainStep("expected", "期望出口 IP", StepStatus.Passed, $"出口 IP 与期望值一致：{exit}", TimeSpan.Zero));
        }
        else
        {
            Add(new ChainStep("expected", "期望出口 IP", StepStatus.Failed, $"出口 IP {exit}（Claude 边缘看到 {edgeIp ?? "未知"}）与期望值 {expectedIp} 不符", TimeSpan.Zero,
                "服务商可能分配了新 IP，或填写的端点不是静态端点。请确认后再激活。"));
        }

        // 6: Claude endpoints
        {
            var sw = Stopwatch.StartNew();
            using var client = ChainHttp.Create(full, timeout, allowRedirects: false);
            var problems = new List<string>();
            foreach (var url in ClaudeProbeUrls)
            {
                try
                {
                    using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
                    // Any HTTP answer (even 4xx) proves TLS reachability through the chain.
                }
                catch (Exception ex)
                {
                    problems.Add($"{new Uri(url).Host}: {ChainHttp.Describe(ex)}");
                }
            }
            Add(problems.Count == 0
                ? new ChainStep("claude", "Claude 服务连通性", StepStatus.Passed, $"claude.ai 与 api.anthropic.com 均可通过专属线路访问（{sw.ElapsedMilliseconds} ms）", sw.Elapsed)
                : new ChainStep("claude", "Claude 服务连通性", StepStatus.Failed, string.Join("；", problems), sw.Elapsed));
        }

        if (lookupIntel && exit != null)
        {
            try { intel = await IpIntelService.LookupAsync(exit, full, timeout, ct).ConfigureAwait(false); }
            catch { /* optional */ }
        }

        return new ChainTestResult(steps, firstExit, exit, exitCountry, edgeIp, edgeCountry, intel);
    }

    private static string ReachHint(bool viaFirstStage) => viaFirstStage
        ? "一级节点可能无法访问该地址/端口（部分节点会屏蔽代理端口），或服务商拒绝了来自该一级出口的连接。可尝试更换一级节点，或在服务商后台确认端口与协议。"
        : "本机无法直接连接该代理服务器：可能被所在网络屏蔽，或地址/端口错误。建议启用一级线路。";

    private static async Task<string?> ProbeTunnelError(IReadOnlyList<ProxyEndpoint> hops, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await using var _ = await ProxyTunnel.ConnectAsync(hops, "www.cloudflare.com", 443, timeout, ct).ConfigureAwait(false);
            return null;
        }
        catch (ProxyTunnelException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
