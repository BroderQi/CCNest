using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CCNest.Core.Proxy;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;

namespace CCNest.Tests;

[Collection("process-state")]
public sealed class ProtectedRouteTests : IDisposable
{
    private const string ExpectedIp = "198.51.100.10";
    private readonly (string Name, string Url)[]? _sources = IpEchoService.TestSources;
    private readonly string[] _claudeUrls = ChainTester.ClaudeProbeUrls;

    public ProtectedRouteTests()
    {
        // These hosts cannot be reached directly. The local simulated core must answer each
        // tunnel itself, making the test independent of DNS, the internet and upstream proxies.
        IpEchoService.TestSources = new[]
        {
            ("echo-a", "http://echo-a.test/"),
            ("echo-b", "http://echo-b.test/"),
            (IpEchoService.ClaudeTraceSource, "http://claude.ai/trace"),
        };
        ChainTester.ClaudeProbeUrls = new[] { "http://claude.ai/", "http://api.anthropic.com/" };
    }

    public void Dispose()
    {
        IpEchoService.TestSources = _sources;
        ChainTester.ClaudeProbeUrls = _claudeUrls;
    }

    [Fact]
    public async Task AllTraffic_UsesCurrentMixedListener_AndDoesNotClaimOriginalStagesPassed()
    {
        await using var mixed = new FakeCoreProxy(_ => ExpectedIp);
        await using var probe = new FakeCoreProxy(_ => throw new InvalidOperationException("unexpected probe"));

        var result = await Run(mixed, probe, RoutingScope.AllTraffic);

        Assert.True(result.Passed, Details(result));
        Assert.Equal(RouteTestMode.Protected, result.Mode);
        Assert.Equal(ExpectedIp, result.ExitIp);
        Assert.Null(result.FirstStageExitIp);
        Assert.Equal(StepStatus.Skipped, Assert.Single(result.Steps, s => s.Id == "original").Status);
        Assert.DoesNotContain(result.Steps, s => s.Id is "first" or "reach" or "auth");
        Assert.Contains("echo-a.test:80", mixed.Targets);
        Assert.Contains("api.anthropic.com:80", mixed.Targets);
        Assert.Empty(probe.Targets);
    }

    [Fact]
    public async Task MinorityEchoMismatch_CannotPassByMajorityConsensus()
    {
        await using var mixed = new FakeCoreProxy(host => host == "echo-b.test" ? "203.0.113.99" : ExpectedIp);
        await using var probe = new FakeCoreProxy(_ => ExpectedIp);

        var result = await Run(mixed, probe, RoutingScope.AllTraffic);

        Assert.Equal(ExpectedIp, result.ExitIp);
        Assert.False(result.Passed);
        var expected = Assert.Single(result.Steps, s => s.Id == "expected");
        Assert.Equal(StepStatus.Failed, expected.Status);
        Assert.Contains("203.0.113.99", expected.Detail);
    }

    [Fact]
    public async Task ClaudeOnly_UsesForcedChainForGeneralEcho_AndMixedForClaude()
    {
        // Unrelated Mixed traffic legitimately leaves through stage 1. It must not be compared
        // to the expected static stage-2 exit, nor used to report that the chain is broken.
        await using var mixed = new FakeCoreProxy(host => host is "claude.ai" or "api.anthropic.com" ? ExpectedIp : "192.0.2.1");
        await using var probe = new FakeCoreProxy(_ => ExpectedIp);

        var result = await Run(mixed, probe, RoutingScope.ClaudeOnly);

        Assert.True(result.Passed, Details(result));
        Assert.Equal(ExpectedIp, result.ExitIp);
        Assert.Equal(ExpectedIp, result.ClaudeEdgeIp);
        Assert.Contains("echo-a.test:80", probe.Targets);
        Assert.Contains("echo-b.test:80", probe.Targets);
        Assert.DoesNotContain(mixed.Targets, t => t.StartsWith("echo-", StringComparison.Ordinal));
        Assert.Contains("claude.ai:80", mixed.Targets);
        Assert.Contains("api.anthropic.com:80", mixed.Targets);
    }

    [Fact]
    public async Task ClaudeOnly_MixedClaudeEdgeMismatch_FailsEvenWhenProbeMatches()
    {
        await using var mixed = new FakeCoreProxy(_ => "203.0.113.77");
        await using var probe = new FakeCoreProxy(_ => ExpectedIp);

        var result = await Run(mixed, probe, RoutingScope.ClaudeOnly);

        Assert.False(result.Passed);
        Assert.Equal(ExpectedIp, result.ExitIp);
        Assert.Equal("203.0.113.77", result.ClaudeEdgeIp);
        Assert.Equal(StepStatus.Failed, Assert.Single(result.Steps, s => s.Id == "expected").Status);
    }

    [Fact]
    public async Task DifferentClaudeExitWithoutExpectedIp_StillFails()
    {
        await using var mixed = new FakeCoreProxy(_ => "203.0.113.77");
        await using var probe = new FakeCoreProxy(_ => ExpectedIp);

        var result = await ProtectedRouteTester.RunAsync(mixed.Port, probe.Port,
            RoutingScope.ClaudeOnly, expectedIp: null, lookupIntel: false);

        Assert.False(result.Passed);
        Assert.Equal(StepStatus.Failed, Assert.Single(result.Steps, s => s.Id == "expected").Status);
    }

    [Fact]
    public async Task ClosedMixedGate_CannotPassBecauseDedicatedProbeSucceeds()
    {
        await using var mixed = new FakeCoreProxy(_ => null);
        await using var probe = new FakeCoreProxy(_ => ExpectedIp);

        var result = await Run(mixed, probe, RoutingScope.ClaudeOnly);

        Assert.False(result.Passed);
        Assert.Equal(ExpectedIp, result.ExitIp);
        Assert.Equal(StepStatus.Passed, Assert.Single(result.Steps, s => s.Id == "exit").Status);
        Assert.Equal(StepStatus.Failed, Assert.Single(result.Steps, s => s.Id == "claude-exit").Status);
        Assert.Equal(StepStatus.Failed, Assert.Single(result.Steps, s => s.Id == "claude").Status);
        Assert.Contains("api.anthropic.com:80", mixed.Targets);
        Assert.DoesNotContain("api.anthropic.com:80", probe.Targets);
    }

    [Fact]
    public async Task UnavailableCoreListener_FailsWithoutTryingAnyRemoteRoute()
    {
        await using var probe = new FakeCoreProxy(_ => ExpectedIp);
        var unavailablePort = NetworkInspector.GetFreePort();

        var result = await ProtectedRouteTester.RunAsync(unavailablePort, probe.Port,
            RoutingScope.ClaudeOnly, ExpectedIp, lookupIntel: false);

        Assert.Equal(RouteTestMode.Protected, result.Mode);
        Assert.False(result.Passed);
        Assert.Equal("core", result.FirstFailure?.Id);
        Assert.Empty(probe.Targets);
    }

    [Fact]
    public async Task ClaudeOnly_UnavailableDedicatedProbe_FailsWithoutUsingMixedAsSubstitute()
    {
        await using var mixed = new FakeCoreProxy(_ => ExpectedIp);

        var result = await ProtectedRouteTester.RunAsync(mixed.Port, NetworkInspector.GetFreePort(),
            RoutingScope.ClaudeOnly, ExpectedIp, lookupIntel: false);

        Assert.False(result.Passed);
        Assert.Equal("core", result.FirstFailure?.Id);
        Assert.Empty(mixed.Targets);
    }

    [Fact]
    public async Task CancelledRequest_PropagatesCancellationInsteadOfReturningFailedSteps()
    {
        await using var mixed = new FakeCoreProxy(_ => ExpectedIp) { Stall = true };
        await using var probe = new FakeCoreProxy(_ => ExpectedIp);
        using var cancelled = new CancellationTokenSource();
        var running = ProtectedRouteTester.RunAsync(mixed.Port, probe.Port,
            RoutingScope.AllTraffic, ExpectedIp, lookupIntel: false, ct: cancelled.Token);
        await mixed.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(probe.Targets);
    }

    private static Task<ChainTestResult> Run(FakeCoreProxy mixed, FakeCoreProxy probe, RoutingScope scope) =>
        ProtectedRouteTester.RunAsync(mixed.Port, probe.Port, scope, ExpectedIp, lookupIntel: false);

    private static string Details(ChainTestResult result) => string.Join(" / ", result.Steps.Select(s => $"{s.Title}:{s.Detail}"));

    /// <summary>
    /// Emulates a core's local HTTP CONNECT listener. Responses come from the listener itself;
    /// it never opens another connection. Returning null emulates the core's closed exit gate.
    /// </summary>
    private sealed class FakeCoreProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, string?> _response;
        private readonly Task _loop;
        private readonly ConcurrentBag<Task> _connections = new();
        public ConcurrentQueue<string> Targets { get; } = new();
        public TaskCompletionSource<bool> FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stall { get; init; }
        public int Port { get; }

        public FakeCoreProxy(Func<string, string?> response)
        {
            _response = response;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await _listener.AcceptSocketAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) when (_stop.IsCancellationRequested) { return; }
                _connections.Add(HandleAsync(socket));
            }
        }

        private async Task HandleAsync(Socket socket)
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            try
            {
                var header = await ReadHeaderAsync(stream, _stop.Token);
                if (header == null) return; // initial local listener check
                var request = header.Split("\r\n")[0].Split(' ');
                if (request[0] != "CONNECT") throw new InvalidOperationException("expected CONNECT");
                var target = request[1];
                Targets.Enqueue(target);
                FirstRequest.TrySetResult(true);
                if (Stall)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }
                var body = _response(target[..target.LastIndexOf(':')]);
                if (body == null)
                {
                    await WriteAsync(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n", _stop.Token);
                    return;
                }
                await WriteAsync(stream, "HTTP/1.1 200 Connection established\r\n\r\n", _stop.Token);
                if (await ReadHeaderAsync(stream, _stop.Token) == null) return;
                await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}", _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { /* the client may close a cancelled request */ }
        }

        private static async Task<string?> ReadHeaderAsync(Stream stream, CancellationToken ct)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 16 * 1024)
            {
                if (await stream.ReadAsync(one, ct) == 0) return null;
                bytes.Add(one[0]);
                if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                    return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new IOException("header too large");
        }

        private static Task WriteAsync(Stream stream, string value, CancellationToken ct) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct).AsTask();

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            await Task.WhenAll(_connections.ToArray());
            _stop.Dispose();
        }
    }
}
