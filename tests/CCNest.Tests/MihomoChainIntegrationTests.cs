using System.Net;
using CCNest.Core.Mihomo;
using CCNest.Core.Proxy;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;
using CCNest.Tests.Fakes;

namespace CCNest.Tests;

/// <summary>
/// End-to-end tests that run the real bundled mihomo binary with CCNest's generated configuration
/// against in-process fake first/second stage proxies. Skipped when the binary is not available.
/// </summary>
[Collection("mihomo")]
public class MihomoChainIntegrationTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public FakeHttpTarget Target = new();
        public FakeHttpProxy First = new("first");
        public FakeSocks5Proxy Second = new("second") { Username = "res-user", Password = "p@ss:w0rd\"'{}" };
        public MihomoSidecar Sidecar = null!;
        public SidecarPorts Ports = null!;

        public static async Task<Rig> StartAsync(RoutingScope scope = RoutingScope.AllTraffic, string? secondPassword = null)
        {
            var rig = new Rig();
            rig.First.RedirectHostnames = rig.Target.EndPoint;
            rig.Second.RedirectHostnames = rig.Target.EndPoint;
            rig.Ports = SidecarPorts.Allocate(NetworkInspector.GetFreePort(), needFirstProbe: true);
            var spec = new CoreConfigSpec
            {
                MixedPort = rig.Ports.Mixed,
                ProbeChainPort = rig.Ports.ProbeChain,
                ProbeFirstPort = rig.Ports.ProbeFirst,
                ControllerPort = rig.Ports.Controller,
                Secret = rig.Ports.Secret,
                FirstStageMode = FirstStageMode.ClashLocalPort,
                FirstStageEndpoint = new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = rig.First.Port },
                SecondStage = new ProxyEndpoint
                {
                    Protocol = ProxyProtocol.Socks5, Host = "127.0.0.1", Port = rig.Second.Port,
                    Username = "res-user", Password = secondPassword ?? "p@ss:w0rd\"'{}",
                },
                Scope = scope,
            };
            var config = MihomoConfigBuilder.Build(spec);
            rig.Sidecar = new MihomoSidecar(TestEnv.MihomoPath!, Path.Combine(TestEnv.NewTempRoot(), "mihomo"));
            var (ok, output) = await rig.Sidecar.TestConfigAsync(config);
            Assert.True(ok, "mihomo -t rejected the generated config: " + output);
            await rig.Sidecar.StartAsync(config, rig.Ports);
            return rig;
        }

        public HttpClient Client(int port) => ChainHttp.ViaLocalProxy(port, TimeSpan.FromSeconds(8));

        public async ValueTask DisposeAsync()
        {
            await Sidecar.DisposeAsync();
            await Second.DisposeAsync();
            await First.DisposeAsync();
            await Target.DisposeAsync();
        }
    }

    [SkippableFactIfNoMihomo]
    public async Task Chain_RoutesThroughFirstThenSecondStage()
    {
        await using var rig = await Rig.StartAsync();
        using var client = rig.Client(rig.Ports.Mixed);
        var body = await client.GetStringAsync("http://chain.test/");
        Assert.Equal("ccnest-target-ok", body);
        Assert.Contains($"127.0.0.1:{rig.Second.Port}", rig.First.Targets);
        Assert.Contains("chain.test:80", rig.Second.Targets);
        Assert.DoesNotContain(rig.First.Targets, t => t.StartsWith("chain.test"));
    }

    [SkippableFactIfNoMihomo]
    public async Task SecondStageDown_FailsClosed_NoDirectOrFirstStageFallback()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Second.DisposeAsync();
        var before = rig.Target.Connections;

        using var client = rig.Client(rig.Ports.Mixed);
        var resp = await TryGet(client, "http://leak.test/");
        Assert.False(resp is { IsSuccessStatusCode: true }, "request must not succeed when the second stage is down");
        Assert.Equal(before, rig.Target.Connections);
        Assert.DoesNotContain(rig.First.Targets, t => t.StartsWith("leak.test"));
    }

    [SkippableFactIfNoMihomo]
    public async Task WrongSecondStagePassword_FailsClosed()
    {
        await using var rig = await Rig.StartAsync(secondPassword: "wrong");
        using var client = rig.Client(rig.Ports.Mixed);
        var resp = await TryGet(client, "http://auth.test/");
        Assert.False(resp is { IsSuccessStatusCode: true });
        Assert.True(rig.Second.AuthFailures > 0);
        Assert.Equal(0, rig.Target.Connections);
    }

    [SkippableFactIfNoMihomo]
    public async Task ClosedGate_BlocksTraffic_ButProbeStillVerifiesChain()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Sidecar.Controller!.SelectProxyAsync(MihomoConfigBuilder.GateName, "REJECT");

        using (var client = rig.Client(rig.Ports.Mixed))
        {
            var resp = await TryGet(client, "http://gated.test/");
            Assert.False(resp is { IsSuccessStatusCode: true });
        }
        Assert.DoesNotContain(rig.Second.Targets, t => t.StartsWith("gated.test"));

        using var probe = rig.Client(rig.Ports.ProbeChain);
        Assert.Equal("ccnest-target-ok", await probe.GetStringAsync("http://probe.test/"));
        Assert.Contains("probe.test:80", rig.Second.Targets);
    }

    [SkippableFactIfNoMihomo]
    public async Task FirstStageProbe_UsesOnlyFirstStage()
    {
        await using var rig = await Rig.StartAsync();
        using var probe = rig.Client(rig.Ports.ProbeFirst!.Value);
        Assert.Equal("ccnest-target-ok", await probe.GetStringAsync("http://first-only.test/"));
        Assert.Contains("first-only.test:80", rig.First.Targets);
        Assert.DoesNotContain(rig.Second.Targets, t => t.StartsWith("first-only.test"));
    }

    [SkippableFactIfNoMihomo]
    public async Task ClaudeOnlyScope_SplitsClaudeDomainsFromOtherTraffic()
    {
        await using var rig = await Rig.StartAsync(RoutingScope.ClaudeOnly);
        using var client = rig.Client(rig.Ports.Mixed);
        Assert.Equal("ccnest-target-ok", await client.GetStringAsync("http://claude.ai/"));
        Assert.Equal("ccnest-target-ok", await client.GetStringAsync("http://api.anthropic.com/"));
        Assert.Equal("ccnest-target-ok", await client.GetStringAsync("http://unrelated.test/"));

        Assert.Contains("claude.ai:80", rig.Second.Targets);
        Assert.Contains("api.anthropic.com:80", rig.Second.Targets);
        Assert.Contains("unrelated.test:80", rig.First.Targets);
        Assert.DoesNotContain(rig.Second.Targets, t => t.StartsWith("unrelated.test"));
    }

    [SkippableFactIfNoMihomo]
    public async Task DelayApi_TestsThroughFullChain()
    {
        await using var rig = await Rig.StartAsync();
        var (delay, error) = await rig.Sidecar.Controller!.DelayAsync(MihomoConfigBuilder.StaticName, "http://delay.test/", 5000);
        Assert.True(delay is > 0, $"delay test failed: {error}");
        Assert.Contains("delay.test:80", rig.Second.Targets);
    }

    [SkippableFactIfNoMihomo]
    public async Task CoreCrash_IsReported_AndPortStopsAccepting()
    {
        await using var rig = await Rig.StartAsync();
        var exited = new TaskCompletionSource<bool>();
        rig.Sidecar.UnexpectedExit += (_, _) => exited.TrySetResult(true);
        System.Diagnostics.Process.GetProcessById(rig.Sidecar.ProcessId!.Value).Kill();
        Assert.True(await Task.WhenAny(exited.Task, Task.Delay(5000)) == exited.Task, "unexpected exit not reported");

        using var client = rig.Client(rig.Ports.Mixed);
        var resp = await TryGet(client, "http://after-crash.test/");
        Assert.Null(resp); // connection refused: apps pointed at CCNest fail closed
        Assert.Equal(0, rig.Target.Connections);
    }

    private static async Task<HttpResponseMessage?> TryGet(HttpClient client, string url)
    {
        try { return await client.GetAsync(url); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }
}

public sealed class SkippableFactIfNoMihomoAttribute : FactAttribute
{
    public SkippableFactIfNoMihomoAttribute()
    {
        if (TestEnv.MihomoPath == null) Skip = "third_party/mihomo/mihomo.exe not found";
    }
}
