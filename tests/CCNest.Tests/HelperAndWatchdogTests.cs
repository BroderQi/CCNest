using System.Diagnostics;
using System.Security.Principal;
using CCNest.Core.Elevation;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using CCNest.Core.Settings;
using CCNest.Core.SystemEnv;

namespace CCNest.Tests;

[Collection("process-state")]
public class HelperAndWatchdogTests
{
    private static string NewPipeName() => $"ccnest-helper-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task HelperPipe_ServesStructuredCommands_ToItsParentOnly()
    {
        var pipe = NewPipeName();
        var server = new HelperServer(Environment.ProcessId, pipe, WindowsIdentity.GetCurrent().User!.Value, CrashPolicy.AutoRestore);
        var run = Task.Run(server.RunAsync);

        // A foreign process (not the parent) must be refused.
        var foreign = Process.Start(new ProcessStartInfo(ProcessRunner.PowerShellPath,
            $"-NoProfile -Command \"$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{pipe}', 'InOut'); $p.Connect(5000); $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true; $w.WriteLine('{{\\\"id\\\":1,\\\"command\\\":\\\"hello\\\"}}'); $r = New-Object System.IO.StreamReader($p); $line = $r.ReadLine(); if ($line) {{ 'GOT:' + $line }} else {{ 'REFUSED' }}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var foreignOutput = await foreign.StandardOutput.ReadToEndAsync();
        await foreign.WaitForExitAsync();
        Assert.DoesNotContain("GOT:", foreignOutput);

        // The parent (this process) is served.
        await using var client = new HelperClient();
        await client.ConnectAsync(pipe, TimeSpan.FromSeconds(10));
        Assert.True(client.IsConnected);

        // Unknown commands are rejected with an error, never executed.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("runCommand", null));
        Assert.Contains("不支持的命令", ex.Message);

        // Unsafe TUN configs are refused before anything is started.
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartTunCoreAsync(new StartTunCoreRequest
        {
            Config = "{\"external-controller\":\"0.0.0.0:9090\",\"secret\":\"" + new string('x', 40) + "\"}",
            SourceBinary = TestEnv.MihomoPath ?? "missing.exe",
        }));
        Assert.Contains("127.0.0.1", bad.Message);

        if (!HelperClient.IsCurrentProcessElevated())
        {
            // Without admin rights WFP operations fail cleanly and are reported back.
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.KillSwitchFilterCountAsync());
        }

        await client.ShutdownAsync(restore: false);
        var finished = await Task.WhenAny(run, Task.Delay(10_000));
        Assert.Same(run, finished);
    }

    private static SessionJournal OrphanJournalWithProxyChange(CrashPolicy policy, FakeProxyBackend backend, int ownerPid)
    {
        var original = new SystemProxyState { ProxyEnabled = false, Bypass = "<local>" };
        var applied = SystemProxy.For(17897);
        backend.State = applied;
        var journal = new SessionJournal { Status = SessionStatus.Active, CrashPolicy = policy, OwnerPid = ownerPid };
        journal.Changes.Add(new SystemProxyChange { Original = original, Applied = applied, State = ChangeState.Applied });
        journal.Changes.Add(new SidecarChange { MixedPort = 17897, State = ChangeState.Applied });
        JournalStore.SaveSession(journal);
        return journal;
    }

    [Fact]
    public async Task Watchdog_AutoRestorePolicy_RestoresWhenParentDies()
    {
        var backend = new FakeProxyBackend();
        var previous = SystemProxy.Backend;
        SystemProxy.Backend = backend;
        try
        {
            using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
            var journal = OrphanJournalWithProxyChange(CrashPolicy.AutoRestore, backend, parent.Id);
            await RecoveryService.RunWatchdogAsync(parent.Id, journal.SessionId);

            Assert.False(backend.State.ProxyEnabled);
            Assert.Null(JournalStore.LoadSession()); // fully restored => closed
        }
        finally
        {
            SystemProxy.Backend = previous;
        }
    }

    [Fact]
    public async Task Watchdog_KeepBlockedPolicy_LeavesFailClosedState()
    {
        var backend = new FakeProxyBackend();
        var previous = SystemProxy.Backend;
        SystemProxy.Backend = backend;
        try
        {
            using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
            var journal = OrphanJournalWithProxyChange(CrashPolicy.KeepBlocked, backend, parent.Id);
            await RecoveryService.RunWatchdogAsync(parent.Id, journal.SessionId);

            Assert.True(backend.State.ProxyEnabled); // still pointing at the (dead) CCNest port
            var left = JournalStore.LoadSession()!;
            Assert.True(left.HasPendingRestore);
            Assert.Equal(ChangeState.Restored, left.Changes.OfType<SidecarChange>().Single().State);

            JournalStore.CloseSession(left);
        }
        finally
        {
            SystemProxy.Backend = previous;
        }
    }

    [Fact]
    public async Task Watchdog_IgnoresCleanShutdown()
    {
        var backend = new FakeProxyBackend();
        var previous = SystemProxy.Backend;
        SystemProxy.Backend = backend;
        try
        {
            using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
            var journal = OrphanJournalWithProxyChange(CrashPolicy.AutoRestore, backend, parent.Id);
            journal.CleanShutdown = true;
            JournalStore.SaveSession(journal);
            await RecoveryService.RunWatchdogAsync(parent.Id, journal.SessionId);
            Assert.True(backend.State.ProxyEnabled); // untouched: nothing to do after a clean shutdown
            JournalStore.CloseSession(journal);
        }
        finally
        {
            SystemProxy.Backend = previous;
        }
    }
}
