using CCNest.Core.Claude;
using CCNest.Core.Diagnostics;
using CCNest.Core.Infrastructure;
using CCNest.Core.Proxy;
using CCNest.Core.Session;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;
using CCNest.Tests.Fakes;

namespace CCNest.Tests;

/// <summary>
/// Full session lifecycle with the real SessionManager and the real mihomo core. The "internet" is a
/// local echo server reached only through a fake static proxy, which therefore acts as a controllable
/// exit whose public IP the test can change (simulating a provider re-assigning the static IP).
/// System-level effects are sandboxed: fake system-proxy backend, temp Claude settings, test Run key.
/// </summary>
[Collection("process-state")]
public class SessionLifecycleTests : IAsyncLifetime
{
    private const string ExitIp = "198.51.100.10";
    private readonly string _root = TestEnv.NewTempRoot();
    private FakeSocks5Proxy _static = null!;
    private FakeHttpTarget _internet = null!;
    private FakeProxyBackend _proxy = null!;
    private string _claudeSettings = "";

    public Task InitializeAsync()
    {
        AppPaths.OverrideRoot(_root);
        StartupRegistration.RemoveRecovery(); // only the sandbox Run key configured by TestEnv
        _proxy = new FakeProxyBackend { State = new SystemProxyState { ProxyEnabled = true, ProxyServer = "corp-proxy:8080", Bypass = "<local>" } };
        SystemProxy.Backend = _proxy;
        _claudeSettings = Path.Combine(_root, "claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_claudeSettings)!);
        File.WriteAllText(_claudeSettings, """{ "model": "sonnet", "env": { "MY_VAR": "1" } }""");
        ClaudeCodeManager.SettingsPathOverride = _claudeSettings;

        _internet = new FakeHttpTarget { Body = ExitIp };
        _static = new FakeSocks5Proxy("static") { Username = "static-user", Password = "static-pass", RedirectHostnames = _internet.EndPoint };
        IpEchoService.TestSources = new[] { ("echo-a", "http://echo-a.test/"), ("echo-b", "http://echo-b.test/"), ("echo-c", "http://echo-c.test/") };
        ChainTester.ClaudeProbeUrls = new[] { "http://claude.test/" };
        SessionManager.HealthCheckUrl = "http://health.test/";
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        StartupRegistration.RemoveRecovery();
        IpEchoService.TestSources = null;
        ChainTester.ClaudeProbeUrls = new[] { "https://claude.ai/cdn-cgi/trace", "https://api.anthropic.com/" };
        SessionManager.HealthCheckUrl = "https://www.gstatic.com/generate_204";
        await _static.DisposeAsync();
        await _internet.DisposeAsync();
        SystemProxy.Backend = new FakeProxyBackend();
        ClaudeCodeManager.SettingsPathOverride = Path.Combine(TestEnv.GlobalRoot, "claude-settings.json");
        AppPaths.OverrideRoot(TestEnv.GlobalRoot);
    }

    private SettingsStore CreateSettings(string? expectedIp, string password = "static-pass")
    {
        var store = new SettingsStore();
        store.Update(s =>
        {
            s.FirstStage.Mode = FirstStageMode.None;
            s.SecondStage.Endpoint = new ProxyEndpoint { Protocol = ProxyProtocol.Socks5, Host = "127.0.0.1", Port = _static.Port, Username = "static-user" };
            s.SecondStage.ExpectedExitIp = expectedIp;
            s.Protection.Level = ProtectionLevel.Standard;
            s.Integrations.SetSystemProxy = true;
            s.Integrations.ConfigureClaudeCode = true;
            s.Advanced.MixedPort = NetworkInspector.GetFreePort();
            s.Advanced.MihomoPathOverride = TestEnv.MihomoPath;
            s.Monitor.HealthIntervalSeconds = 5;
        });
        SecretStore.Set(SecretStore.SecondStagePassword, password);
        return store;
    }

    [SkippableFactIfNoMihomo]
    public async Task ActivateVerifyBlockAndRestore_EndToEnd()
    {
        var store = CreateSettings(ExitIp);
        var originalProxy = _proxy.State;
        await using var session = new SessionManager(store);

        var result = await session.ActivateAsync();
        Assert.True(result.Success, result.Message + " | " + string.Join(" / ", result.Steps.Select(s => $"{s.Title}:{s.Detail}")));
        Assert.Equal(SessionState.Protected, session.Status.State);
        Assert.Equal(ExitIp, session.Status.ExitIp);
        Assert.True(session.Status.GateOpen);

        // Integrations applied with originals journaled first.
        var port = session.MixedPort!.Value;
        Assert.Equal($"127.0.0.1:{port}", _proxy.State.ProxyServer);
        Assert.Equal($"http://127.0.0.1:{port}", ClaudeCodeManager.ReadManagedEnv(_claudeSettings)["HTTPS_PROXY"]);
        Assert.True(StartupRegistration.IsRecoveryRegistered());
        var journal = JournalStore.LoadSession()!;
        Assert.Equal(SessionStatus.Active, journal.Status);
        Assert.All(journal.Changes, c => Assert.Equal(ChangeState.Applied, c.State));

        // Traffic through CCNest's port really leaves through the static proxy.
        using (var client = ChainHttp.ViaLocalProxy(port, TimeSpan.FromSeconds(10)))
            Assert.Equal(ExitIp, await client.GetStringAsync("http://whatismyip.test/"));
        Assert.Contains(_static.Targets, t => t.StartsWith("whatismyip.test"));

        // The provider silently re-assigns the static IP => CCNest blocks instead of continuing.
        _internet.Body = "198.51.100.99";
        Assert.False(await session.ReverifyAsync("测试"));
        Assert.Equal(SessionState.Blocked, session.Status.State);
        Assert.False(session.Status.GateOpen);
        Assert.Equal("198.51.100.99", session.Status.ExitIp);
        var before = _static.Targets.Count;
        using (var client = ChainHttp.ViaLocalProxy(port, TimeSpan.FromSeconds(8)))
        {
            var leaked = await TryGet(client, "http://blocked.test/");
            Assert.False(leaked is { IsSuccessStatusCode: true }, "gate must block traffic while the exit is unverified");
        }
        Assert.DoesNotContain(_static.Targets.Skip(before), t => t.StartsWith("blocked.test"));

        // The user confirms the new IP: expected IP updated, gate reopened.
        Assert.True(await session.AcceptNewExitIpAsync("198.51.100.99"));
        Assert.Equal(SessionState.Protected, session.Status.State);
        Assert.Equal("198.51.100.99", store.Current.SecondStage.ExpectedExitIp);

        // Second stage goes down => monitor reports degraded, traffic fails closed (no fallback).
        await _static.DisposeAsync();
        using (var client = ChainHttp.ViaLocalProxy(port, TimeSpan.FromSeconds(8)))
        {
            var resp = await TryGet(client, "http://down.test/");
            Assert.False(resp is { IsSuccessStatusCode: true });
        }
        var degraded = await WaitForAsync(() => session.Status.State == SessionState.Degraded, TimeSpan.FromSeconds(30));
        Assert.True(degraded, $"expected Degraded, got {session.Status.State}: {session.Status.Detail}");

        // Normal shutdown restores everything.
        var deactivation = await session.DeactivateAsync();
        Assert.True(deactivation.Success, string.Join(" / ", deactivation.Outcomes.Select(o => $"{o.Change.Title}:{o.Message}")));
        Assert.Equal(originalProxy, _proxy.State);
        var after = ClaudeCodeManager.ReadSettings(_claudeSettings);
        Assert.Null(after["env"]!["HTTPS_PROXY"]);
        Assert.Equal("1", after["env"]!["MY_VAR"]!.GetValue<string>());
        Assert.Equal("sonnet", after["model"]!.GetValue<string>());
        Assert.False(StartupRegistration.IsRecoveryRegistered());
        Assert.Null(JournalStore.LoadSession());
        Assert.True(NetworkInspector.IsPortFree(port), "core must be stopped");
        Assert.Equal(SessionState.Inactive, session.Status.State);
    }

    [SkippableFactIfNoMihomo]
    public async Task WrongExpectedIp_AbortsBeforeAnySystemChange()
    {
        var store = CreateSettings("203.0.113.5");
        var originalProxy = _proxy.State;
        var originalSettings = File.ReadAllText(_claudeSettings);
        await using var session = new SessionManager(store);

        var result = await session.ActivateAsync();
        Assert.False(result.Success);
        Assert.Contains("期望", result.Message);
        Assert.Equal(originalProxy, _proxy.State);
        Assert.Equal(originalSettings, File.ReadAllText(_claudeSettings));
        Assert.Null(JournalStore.LoadSession());
        Assert.False(StartupRegistration.IsRecoveryRegistered());
        Assert.True(NetworkInspector.IsPortFree(store.Current.Advanced.MixedPort));
    }

    [SkippableFactIfNoMihomo]
    public async Task WrongStaticPassword_AbortsWithPreciseReason()
    {
        var store = CreateSettings(ExitIp, password: "wrong-pass");
        await using var session = new SessionManager(store);
        var result = await session.ActivateAsync();
        Assert.False(result.Success);
        Assert.Contains("认证", result.Message);
        Assert.Null(JournalStore.LoadSession());
    }

    [SkippableFactIfNoMihomo]
    public async Task CrashedSession_LeavesFailClosedState_ThenRecoveryRestores()
    {
        var store = CreateSettings(ExitIp);
        var originalProxy = _proxy.State;
        var session = new SessionManager(store);
        var result = await session.ActivateAsync();
        Assert.True(result.Success, result.Message);
        var port = session.MixedPort!.Value;

        // Simulate a crash: the core dies (job object) and the journal is left behind by a dead owner.
        await session.DisposeAsync();
        var journal = JournalStore.LoadSession()!;
        journal.OwnerPid = 999_999;
        JournalStore.SaveSession(journal);

        // Fail-closed: the system proxy still points at the now-dead CCNest port, so proxy-aware apps
        // fail instead of silently using another route.
        Assert.Equal($"127.0.0.1:{port}", _proxy.State.ProxyServer);
        Assert.True(NetworkInspector.IsPortFree(port));
        using (var client = ChainHttp.ViaLocalProxy(port, TimeSpan.FromSeconds(5)))
            Assert.Null(await TryGet(client, "http://after-crash.test/"));

        var orphan = JournalStore.FindOrphanedSession();
        Assert.NotNull(orphan);
        var outcomes = await RecoveryService.RestoreOrphanedAsync(orphan!);
        Assert.All(outcomes, o => Assert.True(o.Success, $"{o.Change.Title}: {o.Message}"));
        Assert.Equal(originalProxy, _proxy.State);
        Assert.Null(ClaudeCodeManager.ReadManagedEnv(_claudeSettings)["HTTPS_PROXY"]);
        Assert.Null(JournalStore.FindOrphanedSession());
        Assert.False(StartupRegistration.IsRecoveryRegistered());
    }

    [SkippableFactIfNoMihomo]
    public async Task CurrentRouteTest_UsesRunningSnapshotInsteadOfSavedProxyAndSecrets()
    {
        var store = CreateSettings(ExitIp);
        await using var session = new SessionManager(store);
        var activated = await session.ActivateAsync();
        Assert.True(activated.Success, activated.Message);
        var sessionId = session.Journal!.SessionId;

        // These settings are for the next activation, and cannot be reached or authenticated.
        store.Update(s =>
        {
            s.FirstStage.Mode = FirstStageMode.ClashLocalPort;
            s.FirstStage.LocalPort = 1;
            s.SecondStage.Endpoint = new ProxyEndpoint { Host = "next-activation.invalid", Port = 1, Username = "different-user" };
            s.SecondStage.ExpectedExitIp = "203.0.113.90";
            s.Protection.Scope = RoutingScope.ClaudeOnly;
        });
        SecretStore.Set(SecretStore.SecondStagePassword, "different-password");

        var result = await session.TestCurrentRouteAsync(lookupIntel: false);

        Assert.True(result.Passed, string.Join(" / ", result.Steps.Select(s => s.Detail)));
        Assert.Equal(RouteTestMode.Protected, result.Mode);
        Assert.Equal(ExitIp, result.ExitIp);
        Assert.Equal(sessionId, session.Journal.SessionId);
        Assert.True(session.Status.GateOpen);
        Assert.Equal("203.0.113.90", store.Current.SecondStage.ExpectedExitIp);
        var running = session.GetActiveSettingsSnapshot()!;
        Assert.Equal(FirstStageMode.None, running.FirstStage.Mode);
        Assert.Equal(RoutingScope.AllTraffic, running.Protection.Scope);
        Assert.Equal(ExitIp, running.SecondStage.ExpectedExitIp);

        var diagnostics = await new DiagnosticsEngine(store, session).RunAsync(new DiagnosticsOptions
        {
            IncludeDirectChecks = false,
            IncludeDnsLeakTest = false,
        });
        var chain = Assert.Single(diagnostics.Items, item => item.Id == "chain");
        Assert.Equal(DiagStatus.Ok, chain.Status);
        Assert.Contains("当前", chain.Title);
        Assert.Contains(ExitIp, chain.Detected);
        Assert.True((await session.DeactivateAsync()).Success);
    }

    [SkippableFactIfNoMihomo]
    public async Task CurrentRouteTest_BlockedSessionDoesNotUseSuccessfulProbeOrOpenGate()
    {
        var store = CreateSettings(ExitIp);
        await using var session = new SessionManager(store);
        var activated = await session.ActivateAsync();
        Assert.True(activated.Success, activated.Message);
        _internet.Body = "203.0.113.91";
        Assert.False(await session.ReverifyAsync("test changed exit"));
        Assert.Equal(SessionState.Blocked, session.Status.State);

        // The diagnostic probe bypasses the gate and can work while users remain blocked.
        var probe = new[] { new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = session.ProbePort!.Value } };
        Assert.True((await IpEchoService.ObserveAsync(probe, TimeSpan.FromSeconds(5), includeClaude: false)).AnySuccess);
        var result = await session.TestCurrentRouteAsync(lookupIntel: false);

        Assert.False(result.Passed);
        Assert.Equal(RouteTestMode.Protected, result.Mode);
        Assert.Equal("current", result.FirstFailure?.Id);
        Assert.Null(result.ExitIp);
        Assert.Equal(SessionState.Blocked, session.Status.State);
        Assert.False(session.Status.GateOpen);
        Assert.True((await session.DeactivateAsync()).Success);
    }

    [Fact]
    public async Task CurrentRouteTest_InactiveSessionDoesNotFallBackToOriginalSettings()
    {
        await using var session = new SessionManager(CreateSettings(ExitIp));

        var result = await session.TestCurrentRouteAsync(lookupIntel: false);

        Assert.False(session.HasSession);
        Assert.False(result.Passed);
        Assert.Equal(RouteTestMode.Protected, result.Mode);
        Assert.Empty(_static.Targets);
    }

    [SkippableFactIfNoMihomo]
    public async Task CurrentRouteTest_SessionEndedDuringProbeCannotReportSuccess()
    {
        await using var session = new SessionManager(CreateSettings(ExitIp));
        var activated = await session.ActivateAsync();
        Assert.True(activated.Success, activated.Message);
        var progress = new InlineProgress(step =>
        {
            if (step.Id == "original") session.DeactivateAsync().GetAwaiter().GetResult();
        });

        var result = await session.TestCurrentRouteAsync(progress, lookupIntel: false);

        Assert.False(result.Passed);
        Assert.False(session.HasSession);
        Assert.Contains(result.Steps, s => s.Id == "session-changed" && s.Status == StepStatus.Failed);
        Assert.Null(result.Intel);
    }

    private sealed class InlineProgress(Action<ChainStep> report) : IProgress<ChainStep>
    {
        public void Report(ChainStep value) => report(value);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }
        return condition();
    }

    private static async Task<HttpResponseMessage?> TryGet(HttpClient client, string url)
    {
        try { return await client.GetAsync(url); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }
}
