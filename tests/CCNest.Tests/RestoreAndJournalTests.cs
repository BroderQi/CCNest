using System.Text.Json;
using System.Text.Json.Nodes;
using CCNest.Core.Claude;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using CCNest.Core.SystemEnv;
using CCNest.Core.Wfp;

namespace CCNest.Tests;

internal sealed class FakeProxyBackend : ISystemProxyBackend
{
    public SystemProxyState State = new() { ProxyEnabled = false, Bypass = "<local>" };
    public SystemProxyState Read() => State;
    public void Write(SystemProxyState state) => State = state;
}

[Collection("process-state")]
public class RestoreAndJournalTests : IDisposable
{
    private readonly string _root = TestEnv.NewTempRoot();
    private readonly ISystemProxyBackend _originalBackend = SystemProxy.Backend;

    public RestoreAndJournalTests()
    {
        AppPaths.OverrideRoot(_root);
    }

    public void Dispose()
    {
        SystemProxy.Backend = _originalBackend;
        ClaudeCodeManager.SettingsPathOverride = Path.Combine(TestEnv.GlobalRoot, "claude-settings.json");
        AppPaths.OverrideRoot(TestEnv.GlobalRoot);
    }

    [Fact]
    public async Task SystemProxy_RestoresOriginal()
    {
        var fake = new FakeProxyBackend { State = new SystemProxyState { ProxyEnabled = true, ProxyServer = "127.0.0.1:7897", Bypass = "<local>" } };
        SystemProxy.Backend = fake;
        var change = new SystemProxyChange { Original = fake.State, Applied = SystemProxy.For(17897), State = ChangeState.Applied };
        fake.Write(change.Applied);

        var outcome = await new ChangeExecutor(null).RestoreAsync(change);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("127.0.0.1:7897", fake.State.ProxyServer);
        Assert.Equal(ChangeState.Restored, change.State);
    }

    [Fact]
    public async Task SystemProxy_ModifiedByOthers_IsNotOverwritten()
    {
        var fake = new FakeProxyBackend();
        SystemProxy.Backend = fake;
        var change = new SystemProxyChange { Original = fake.State, Applied = SystemProxy.For(17897), State = ChangeState.Applied };
        fake.Write(new SystemProxyState { ProxyEnabled = true, ProxyServer = "127.0.0.1:9999" }); // someone else changed it

        var outcome = await new ChangeExecutor(null).RestoreAsync(change);
        Assert.False(outcome.Success);
        Assert.Equal(ChangeState.Conflict, change.State);
        Assert.Equal("127.0.0.1:9999", fake.State.ProxyServer);

        var forced = await new ChangeExecutor(null).RestoreAsync(change, force: true);
        Assert.True(forced.Success);
        Assert.False(fake.State.ProxyEnabled);
    }

    [Fact]
    public async Task ClaudeSettings_MergeAndRestore_PreservesUnrelatedKeys()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{ "model": "opus", "permissions": { "allow": ["Bash(ls)"] }, "env": { "FOO": "bar", "NO_PROXY": "corp.local" } }""");
        ClaudeCodeManager.SettingsPathOverride = path;

        var original = ClaudeCodeManager.ReadManagedEnv(path);
        var applied = ClaudeCodeManager.ProxyEnvFor(17897, original["NO_PROXY"]);
        ClaudeCodeManager.WriteManagedEnv(path, applied, removeEmptyEnv: false);

        var during = ClaudeCodeManager.ReadSettings(path);
        Assert.Equal("http://127.0.0.1:17897", during["env"]!["HTTPS_PROXY"]!.GetValue<string>());
        Assert.Equal("corp.local,localhost,127.0.0.1,::1", during["env"]!["NO_PROXY"]!.GetValue<string>());
        Assert.Equal("bar", during["env"]!["FOO"]!.GetValue<string>());
        Assert.Equal("opus", during["model"]!.GetValue<string>());

        var change = new ClaudeCodeEnvChange { SettingsPath = path, FileExisted = true, EnvExisted = true, Original = original, Applied = applied, State = ChangeState.Applied };
        var outcome = await new ChangeExecutor(null).RestoreAsync(change);
        Assert.True(outcome.Success, outcome.Message);

        var after = ClaudeCodeManager.ReadSettings(path);
        Assert.Null(after["env"]!["HTTPS_PROXY"]);
        Assert.Null(after["env"]!["HTTP_PROXY"]);
        Assert.Equal("corp.local", after["env"]!["NO_PROXY"]!.GetValue<string>());
        Assert.Equal("bar", after["env"]!["FOO"]!.GetValue<string>());
        Assert.Equal("Bash(ls)", after["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task ClaudeSettings_UserEditDuringSession_IsReportedAsConflict()
    {
        var path = Path.Combine(_root, "settings2.json");
        File.WriteAllText(path, "{}");
        var original = ClaudeCodeManager.ReadManagedEnv(path);
        var applied = ClaudeCodeManager.ProxyEnvFor(17897, null);
        ClaudeCodeManager.WriteManagedEnv(path, applied, removeEmptyEnv: false);
        ClaudeCodeManager.WriteManagedEnv(path, new Dictionary<string, string?> { ["HTTPS_PROXY"] = "http://corp-proxy:8080" }, false);

        var change = new ClaudeCodeEnvChange { SettingsPath = path, FileExisted = true, EnvExisted = false, Original = original, Applied = applied, State = ChangeState.Applied };
        var outcome = await new ChangeExecutor(null).RestoreAsync(change);
        Assert.Equal(ChangeState.Conflict, change.State);
        var after = ClaudeCodeManager.ReadSettings(path);
        Assert.Equal("http://corp-proxy:8080", after["env"]!["HTTPS_PROXY"]!.GetValue<string>()); // user's value kept
        Assert.Null(after["env"]!["HTTP_PROXY"]); // CCNest's own value removed
    }

    [Fact]
    public void ClaudeSettings_WithComments_AreNeverRewritten()
    {
        var path = Path.Combine(_root, "settings3.json");
        var text = "{\n  // my comment\n  \"model\": \"opus\",\n}";
        File.WriteAllText(path, text);
        Assert.Throws<InvalidDataException>(() => ClaudeCodeManager.WriteManagedEnv(path, ClaudeCodeManager.ProxyEnvFor(1, null), false));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void Journal_RoundTripsPolymorphicChanges()
    {
        var journal = new SessionJournal { ExpectedExitIp = "203.0.113.7" };
        journal.Changes.Add(new SystemProxyChange { Original = new SystemProxyState { ProxyEnabled = false }, Applied = SystemProxy.For(1), State = ChangeState.Applied });
        journal.Changes.Add(new KillSwitchChange { Mode = KillSwitchMode.SystemWide, Dynamic = false });
        journal.Changes.Add(new UserEnvChange { Name = "HTTPS_PROXY", Original = null, Applied = "http://127.0.0.1:1" });
        journal.Changes.Add(new LanguageListChange { Original = new() { new LanguageEntry("zh-Hans-CN", new[] { "0804:{X}" }) }, AppliedTop = "en-US" });
        JournalStore.SaveSession(journal);

        var loaded = JournalStore.LoadSession()!;
        Assert.Equal(4, loaded.Changes.Count);
        Assert.IsType<SystemProxyChange>(loaded.Changes[0]);
        Assert.Equal(KillSwitchMode.SystemWide, Assert.IsType<KillSwitchChange>(loaded.Changes[1]).Mode);
        Assert.Equal("0804:{X}", Assert.IsType<LanguageListChange>(loaded.Changes[3]).Original[0].InputMethodTips[0]);
        Assert.True(loaded.HasPendingRestore);
        Assert.Equal("203.0.113.7", loaded.ExpectedExitIp);
    }

    [Fact]
    public void OrphanDetection_IgnoresLiveOwner_AndFindsDeadOwner()
    {
        var journal = new SessionJournal { Status = SessionStatus.Active };
        journal.Changes.Add(new SystemProxyChange { State = ChangeState.Applied });
        journal.OwnerPid = Environment.ProcessId;
        JournalStore.SaveSession(journal);
        Assert.Null(JournalStore.FindOrphanedSession()); // our own session

        journal.OwnerPid = 999_999; // no such process
        JournalStore.SaveSession(journal);
        Assert.NotNull(JournalStore.FindOrphanedSession());

        JournalStore.CloseSession(journal);
        Assert.Null(JournalStore.LoadSession());
    }

    [Fact]
    public void SecretStore_RoundTripsWithDpapi_AndRedacts()
    {
        SecretStore.Set("test-secret", "pa$$-wörd-123");
        Assert.Equal("pa$$-wörd-123", SecretStore.Get("test-secret"));
        var raw = File.ReadAllBytes(Path.Combine(AppPaths.SecretsDir, "test-secret.dpapi"));
        Assert.DoesNotContain("pa$$", System.Text.Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("pa$$-wörd-123", SecretRedactor.Redact("value=pa$$-wörd-123 done"));
        SecretStore.Set("test-secret", null);
        Assert.Null(SecretStore.Get("test-secret"));
    }

    [Fact]
    public async Task UserEnv_SandboxedRegistry_SetAndRestore()
    {
        var originalPath = EnvironmentVars.UserKeyPath;
        EnvironmentVars.UserKeyPath = @"Software\CCNestTests\Env-" + Guid.NewGuid().ToString("N");
        try
        {
            EnvironmentVars.SetUser("HTTPS_PROXY", "http://127.0.0.1:1");
            Assert.Equal("http://127.0.0.1:1", EnvironmentVars.GetUser("https_proxy"));
            var change = new UserEnvChange { Name = "HTTPS_PROXY", Original = null, Applied = "http://127.0.0.1:1", State = ChangeState.Applied };
            var outcome = await new ChangeExecutor(null).RestoreAsync(change);
            Assert.True(outcome.Success);
            Assert.Null(EnvironmentVars.GetUser("HTTPS_PROXY"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(EnvironmentVars.UserKeyPath, false);
            EnvironmentVars.UserKeyPath = originalPath;
        }
    }
}
