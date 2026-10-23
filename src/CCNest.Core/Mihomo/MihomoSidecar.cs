using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CCNest.Core.Infrastructure;
using CCNest.Core.SystemEnv;

namespace CCNest.Core.Mihomo;

public sealed record SidecarPorts(int Mixed, int ProbeChain, int? ProbeFirst, int Controller, string Secret)
{
    /// <summary>Allocates free loopback ports for the probes/controller and a random 256-bit controller secret.</summary>
    public static SidecarPorts Allocate(int mixedPort, bool needFirstProbe)
    {
        var used = new HashSet<int> { mixedPort };
        int Next()
        {
            for (var i = 0; i < 50; i++)
            {
                var p = NetworkInspector.GetFreePort();
                if (used.Add(p)) return p;
            }
            throw new InvalidOperationException("无法分配本地端口");
        }
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new SidecarPorts(mixedPort, Next(), needFirstProbe ? Next() : null, Next(), secret);
    }
}

/// <summary>
/// Runs CCNest's private mihomo instance. The configuration (which contains proxy credentials) is
/// passed through stdin ("-f -"), so it is never written to disk, the command line or the
/// environment. The process is tied to a kill-on-close job object: if CCNest dies, so does the core,
/// which makes every app pointed at CCNest's port fail closed instead of leaking.
/// </summary>
public sealed class MihomoSidecar : IAsyncDisposable
{
    private readonly string _binary;
    private readonly string _homeDir;
    private readonly bool _useJob;
    private JobObject? _job;
    private Process? _process;
    private readonly Queue<string> _recentOutput = new();
    private volatile bool _stopping;

    public MihomoSidecar(string binary, string homeDir, bool useJobObject = true)
    {
        _binary = binary;
        _homeDir = homeDir;
        _useJob = useJobObject;
    }

    public SidecarPorts? Ports { get; private set; }
    public MihomoController? Controller { get; private set; }
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Raised when the core exits without <see cref="StopAsync"/> having been called.</summary>
    public event Action<int?, string>? UnexpectedExit;

    public string RecentOutput
    {
        get { lock (_recentOutput) return string.Join(Environment.NewLine, _recentOutput); }
    }

    public static string? VerifyBinary(string path)
    {
        if (!File.Exists(path)) return $"找不到代理内核：{path}";
        return null;
    }

    /// <summary>Validates a configuration with "mihomo -t" without starting anything.</summary>
    public async Task<(bool Ok, string Output)> TestConfigAsync(string config, CancellationToken ct = default)
    {
        var psi = CreateStartInfo(test: true);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动内核");
        await p.StandardInput.WriteAsync(config).ConfigureAwait(false);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (false, "配置校验超时"); }
        var output = SecretRedactor.Redact((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        return (p.ExitCode == 0, output.Trim());
    }

    public async Task StartAsync(string config, SidecarPorts ports, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("内核已在运行");
        var err = VerifyBinary(_binary);
        if (err != null) throw new FileNotFoundException(err);
        Directory.CreateDirectory(_homeDir);
        Ports = ports;
        SecretRedactor.Register(ports.Secret);
        _stopping = false;

        var psi = CreateStartInfo(test: false);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        process.Exited += (_, _) => OnExited(process);

        if (!process.Start()) throw new InvalidOperationException("无法启动代理内核");
        _process = process;
        if (_useJob)
        {
            _job ??= new JobObject();
            try { _job.Assign(process); }
            catch (Exception ex) { Log.Warn("Sidecar", $"无法将内核加入作业对象：{ex.Message}"); }
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.StandardInput.WriteAsync(config).ConfigureAwait(false);
        process.StandardInput.Close();
        Log.Info("Sidecar", $"代理内核已启动 PID={process.Id}，混合端口 {ports.Mixed}");

        Controller = new MihomoController($"127.0.0.1:{ports.Controller}", ports.Secret, TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"代理内核启动后立即退出（代码 {process.ExitCode}）：{LastLines(6)}");
            try
            {
                var v = await Controller.GetVersionAsync(ct).ConfigureAwait(false);
                Log.Info("Sidecar", $"内核控制接口就绪：mihomo {v.Version}");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        await StopAsync().ConfigureAwait(false);
        throw new TimeoutException($"代理内核未能在 20 秒内就绪：{last?.Message} {LastLines(4)}");
    }

    public async Task StopAsync()
    {
        _stopping = true;
        var p = _process;
        if (p != null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Sidecar", $"停止内核时出错：{ex.Message}");
            }
            Log.Info("Sidecar", "代理内核已停止");
        }
        Controller?.Dispose();
        Controller = null;
        _process = null;
        if (Ports != null) SecretRedactor.Unregister(Ports.Secret);
    }

    private ProcessStartInfo CreateStartInfo(bool test)
    {
        Directory.CreateDirectory(_homeDir);
        var psi = new ProcessStartInfo(_binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _homeDir,
        };
        if (test) psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(_homeDir);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("-");
        // Make sure no inherited override variables can change the core's behaviour.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("CLASH_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        return psi;
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var clean = SecretRedactor.Redact(line);
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(clean);
            while (_recentOutput.Count > 200) _recentOutput.Dequeue();
        }
        var level = clean.Contains("level=error", StringComparison.OrdinalIgnoreCase) || clean.Contains("level=fatal", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Error
            : clean.Contains("level=warn", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warn : LogLevel.Debug;
        Log.Write(level, "mihomo", clean);
    }

    private void OnExited(Process p)
    {
        int? code = null;
        try { code = p.ExitCode; } catch { /* ignore */ }
        if (_stopping) return;
        Log.Error("Sidecar", $"代理内核意外退出（代码 {code}）：{LastLines(4)}");
        UnexpectedExit?.Invoke(code, LastLines(6));
    }

    private string LastLines(int n)
    {
        lock (_recentOutput) return string.Join(" | ", _recentOutput.TakeLast(n));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _job?.Dispose();
        _job = null;
    }
}
