using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CCNest.Core.Settings;

namespace CCNest.Core.Proxy;

/// <summary>
/// Tests a running protected session exclusively through its local mihomo listeners. It never
/// connects to the configured first/second stage itself or changes the gate or protection rules.
/// </summary>
public static class ProtectedRouteTester
{
    public static async Task<ChainTestResult> RunAsync(int mixedPort, int chainProbePort, RoutingScope scope,
        string? expectedIp, IProgress<ChainStep>? progress = null, bool lookupIntel = true, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var steps = new List<ChainStep>();
        var timeout = TimeSpan.FromSeconds(12);
        string? exit = null, country = null, edgeIp = null, edgeCountry = null;
        IpIntel? intel = null;

        void Add(ChainStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        ChainTestResult Result() => new(steps, null, exit, country, edgeIp, edgeCountry, intel)
        {
            Mode = RouteTestMode.Protected,
        };

        Add(new ChainStep("original", "原始线路逐级测试", StepStatus.Skipped,
            "当前环境已启动，本次通过运行中的 CCNest 内核检测受保护线路；未直接测试一级端口、二级服务器或其认证。", TimeSpan.Zero));

        // Merely opening the listeners proves only that the core accepts local connections.
        // Successful requests through Mixed below are also required, including in ClaudeOnly mode.
        var sw = Stopwatch.StartNew();
        try
        {
            await CheckListenerAsync(mixedPort, timeout, ct).ConfigureAwait(false);
            if (scope == RoutingScope.ClaudeOnly && chainProbePort != mixedPort)
                await CheckListenerAsync(chainProbePort, timeout, ct).ConfigureAwait(false);
            Add(new ChainStep("core", "当前 CCNest 内核", StepStatus.Passed,
                "当前内核的本地检测入口可连接", sw.Elapsed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Add(new ChainStep("core", "当前 CCNest 内核", StepStatus.Failed,
                $"无法连接当前内核的本地入口：{ChainHttp.Describe(ex)}", sw.Elapsed,
                "请检查当前环境和内核运行状态；本次未绕过保护连接上游代理。"));
            return Result();
        }

        var mixed = LocalRoute(mixedPort);
        var chainProbe = LocalRoute(chainProbePort);
        var echoRoute = scope == RoutingScope.ClaudeOnly ? chainProbe : mixed;
        var observations = new List<IpObservation>();

        sw.Restart();
        var echo = await IpEchoService.ObserveAsync(echoRoute, timeout, includeClaude: true, ct).ConfigureAwait(false);
        observations.AddRange(scope == RoutingScope.ClaudeOnly
            ? echo.Observations.Select(o => o with { Source = $"链路探测/{o.Source}" })
            : echo.Observations);
        exit = echo.ConsensusIp;
        country = echo.Observations.FirstOrDefault(o => o.Success && o.Country != null)?.Country;
        Add(new ChainStep("exit", "受保护链路出口", !echo.AnySuccess || echo.Inconsistent ? StepStatus.Failed : StepStatus.Passed,
            !echo.AnySuccess ? echo.Summary
                : echo.Inconsistent ? $"不同检测源的出口不一致：{echo.Summary}"
                : $"{(scope == RoutingScope.ClaudeOnly ? "经内核专用探测入口" : "经当前流量入口")}核对出口 IP：{exit}", sw.Elapsed,
            !echo.AnySuccess ? "请检查当前内核、代理链路或出口闸门状态。" : null));

        if (scope == RoutingScope.ClaudeOnly)
        {
            // Ordinary IP echo sites intentionally use stage 1 on Mixed in this mode. Their IPs
            // must not be mistaken for the Claude exit, or compared with the static expected IP.
            sw.Restart();
            var claude = await IpEchoService.ObserveClaudeAsync(mixed, timeout, ct).ConfigureAwait(false);
            observations.AddRange(claude.Observations);
            var edge = claude.Observations.FirstOrDefault(o => o.Success);
            edgeIp = edge?.Ip;
            edgeCountry = edge?.Country;
            Add(new ChainStep("claude-exit", "当前 Claude 线路出口", claude.AnySuccess ? StepStatus.Passed : StepStatus.Failed,
                claude.AnySuccess ? $"通过当前流量入口，Claude 边缘看到的 IP：{edgeIp}"
                    : $"无法核对当前 Claude 线路的出口：{claude.Summary}", sw.Elapsed,
                !claude.AnySuccess ? "专用探测入口成功不代表 Claude 当前线路可用；请检查出口闸门和 Claude 分流规则。" : null));
        }
        else
        {
            var edge = echo.Observations.FirstOrDefault(o => o.Source == IpEchoService.ClaudeTraceSource && o.Success);
            edgeIp = edge?.Ip;
            edgeCountry = edge?.Country;
        }

        var successful = observations.Where(o => o.Success).ToList();
        if (string.IsNullOrWhiteSpace(expectedIp))
        {
            var inconsistent = successful.Any(o => !IpEchoService.SameIp(exit, o.Ip));
            Add(new ChainStep("expected", "期望出口 IP", inconsistent ? StepStatus.Failed : StepStatus.Warning,
                inconsistent ? $"链路与 Claude 出口不一致：{Describe(successful)}"
                    : $"尚未设置期望出口 IP；当前检测出口为 {exit ?? "未知"}。", TimeSpan.Zero));
        }
        else
        {
            var matches = successful.Count > 0 && successful.All(o => IpEchoService.SameIp(expectedIp, o.Ip));
            Add(new ChainStep("expected", "期望出口 IP", matches ? StepStatus.Passed : StepStatus.Failed,
                matches ? $"所有成功检测源的出口均与期望值一致：{expectedIp}"
                    : $"期望出口 {expectedIp} 与检测结果不符：{Describe(successful)}", TimeSpan.Zero,
                matches ? null : "请核对服务商分配的出口和当前内核配置；本次检测不会更改期望 IP 或开启闸门。"));
        }

        sw.Restart();
        var problems = new List<string>();
        using (var client = ChainHttp.Create(mixed, timeout, allowRedirects: false))
        {
            foreach (var url in ChainTester.ClaudeProbeUrls)
            {
                try
                {
                    // Any HTTP response, including an account-related 4xx, proves reachability.
                    using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    problems.Add($"{new Uri(url).Host}: {ChainHttp.Describe(ex)}");
                }
            }
        }
        Add(new ChainStep("claude", "Claude 当前服务连通性", problems.Count == 0 ? StepStatus.Passed : StepStatus.Failed,
            problems.Count == 0 ? "Claude 服务可通过当前受保护流量入口访问（仅验证网络连通性）"
                : string.Join("；", problems), sw.Elapsed));

        if (lookupIntel && exit != null)
        {
            // Force stage 2 even when the user routing rules send unrelated domains to stage 1.
            try { intel = await IpIntelService.LookupAsync(exit, chainProbe, timeout, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* optional metadata */ }
        }
        ct.ThrowIfCancellationRequested();
        return Result();
    }

    private static ProxyEndpoint[] LocalRoute(int port) =>
        new[] { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = port } };

    private static async Task CheckListenerAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "内核端口无效");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), deadline.Token).ConfigureAwait(false);
    }

    private static string Describe(IEnumerable<IpObservation> observations) =>
        string.Join("；", observations.Select(o => $"{o.Source}={o.Ip}")) is { Length: > 0 } summary ? summary : "没有成功的出口检测结果";
}
