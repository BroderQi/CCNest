using System.Diagnostics;
using System.Text;

namespace CCNest.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a small, fixed set of local system tools (PowerShell with embedded scripts, tzutil).
/// CCNest never runs commands obtained from remote sources.
/// </summary>
public static class ProcessRunner
{
    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout,
        IDictionary<string, string?>? environment = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            proc.WaitForExit(); // flush async output
            return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), true);
        }
    }

    /// <summary>
    /// Runs an embedded PowerShell script. The script text is passed via -EncodedCommand so no
    /// quoting issues can alter it; dynamic values are passed as environment variables and read
    /// with $env:..., never concatenated into the script.
    /// </summary>
    public static Task<ProcessResult> PowerShellAsync(string script, IDictionary<string, string?>? env = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var prelude = "$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude + script));
        return RunAsync(PowerShellPath,
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
            timeout ?? TimeSpan.FromSeconds(60), env, ct);
    }
}
