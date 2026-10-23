using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using CCNest.Core.Settings;

namespace CCNest.Core.Elevation;

public sealed class ElevationDeniedException : Exception
{
    public ElevationDeniedException() : base("未获得管理员授权（用户取消了 UAC 提示）") { }
}

/// <summary>
/// UI-side client for the elevated helper. The helper is launched with the "runas" verb, which shows
/// the standard Windows UAC prompt; CCNest itself always runs as a standard user.
/// </summary>
public sealed class HelperClient : IElevationBroker, IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _helperProcess;

    public bool IsConnected => _pipe is { IsConnected: true };
    public bool IsAvailable => IsConnected;

    public static bool IsCurrentProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task EnsureStartedAsync(CrashPolicy policy, CancellationToken ct = default)
    {
        if (IsConnected) return;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
        var pipeName = $"ccnest-helper-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var sid = WindowsIdentity.GetCurrent().User!.Value;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--helper --parent {Environment.ProcessId} --pipe {pipeName} --client-sid {sid} --policy {policy}",
        };
        try
        {
            _helperProcess = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ElevationDeniedException();
        }

        await ConnectAsync(pipeName, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    /// <summary>Connects to an already running helper pipe (used after launch, and by tests).</summary>
    internal async Task ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var hello = await SendAsync(HelperCommands.Hello, null, ct).ConfigureAwait(false);
        Log.Info("Elevation", $"提权助手已连接（PID {hello?["pid"]}）");
    }

    public async Task<JsonNode?> SendAsync(string command, JsonNode? payload, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("提权助手未连接");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = new HelperRequest { Id = Interlocked.Increment(ref _nextId), Command = command, Payload = payload };
            await _writer!.WriteLineAsync(HelperJson.Serialize(request)).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            var line = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false) ?? throw new IOException("提权助手已断开");
            var response = HelperJson.Deserialize<HelperResponse>(line) ?? throw new IOException("提权助手响应无效");
            if (!response.Ok) throw new InvalidOperationException(response.Error ?? "提权操作失败");
            return response.Payload;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<JsonNode?> ApplyKillSwitchAsync(KillSwitchRequest request, CancellationToken ct = default) =>
        SendAsync(HelperCommands.ApplyKillSwitch, HelperJson.ToNode(request), ct);

    public async Task RemoveKillSwitchAsync(CancellationToken ct = default) =>
        await SendAsync(HelperCommands.RemoveKillSwitch, null, ct).ConfigureAwait(false);

    public async Task<int> KillSwitchFilterCountAsync(CancellationToken ct = default) =>
        (await SendAsync(HelperCommands.KillSwitchStatus, null, ct).ConfigureAwait(false))?["filters"]?.GetValue<int>() ?? 0;

    public Task<JsonNode?> StartTunCoreAsync(StartTunCoreRequest request, CancellationToken ct = default) =>
        SendAsync(HelperCommands.StartTunCore, HelperJson.ToNode(request), ct);

    public async Task StopTunCoreAsync(CancellationToken ct = default) =>
        await SendAsync(HelperCommands.StopTunCore, null, ct).ConfigureAwait(false);

    public async Task<bool> TunCoreRunningAsync(CancellationToken ct = default) =>
        (await SendAsync(HelperCommands.TunCoreStatus, null, ct).ConfigureAwait(false))?["running"]?.GetValue<bool>() ?? false;

    public async Task SetCrashPolicyAsync(CrashPolicy policy, CancellationToken ct = default) =>
        await SendAsync(HelperCommands.SetCrashPolicy, HelperJson.ToNode(new CrashPolicyRequest { Policy = policy }), ct).ConfigureAwait(false);

    /// <summary>Asks the helper to exit. With restore=true it first stops the TUN core and removes WFP rules.</summary>
    public async Task ShutdownAsync(bool restore, CancellationToken ct = default)
    {
        if (!IsConnected) return;
        try
        {
            await SendAsync(HelperCommands.Shutdown, new JsonObject { ["restore"] = restore }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Elevation", $"关闭提权助手时出错：{ex.Message}");
        }
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task DisconnectAsync()
    {
        _reader?.Dispose();
        if (_writer != null) await _writer.DisposeAsync().ConfigureAwait(false);
        if (_pipe != null) await _pipe.DisposeAsync().ConfigureAwait(false);
        _pipe = null;
        _reader = null;
        _writer = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
