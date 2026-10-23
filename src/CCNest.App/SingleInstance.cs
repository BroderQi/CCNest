using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using CCNest.Core.Infrastructure;

namespace CCNest.App;

/// <summary>
/// Ensures a single UI instance per Windows user. A second launch forwards a small allow-listed set of
/// flags (e.g. --open-browser from the desktop shortcut) to the running instance and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string[] AllowedFlags = { "--open-browser", "--recover", "--show", "--activate" };
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "user";
        _mutex = new Mutex(true, $@"Local\CCNest.UI.{sid}", out var created);
        IsFirstInstance = created;
        _pipeName = $"CCNest.UI.{sid}";
    }

    public bool IsFirstInstance { get; }

    public event Action<string[]>? ArgumentsReceived;

    public void ForwardToFirstInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            var forwarded = args.Where(a => AllowedFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).DefaultIfEmpty("--show");
            writer.WriteLine(string.Join(" ", forwarded));
        }
        catch (Exception ex)
        {
            Log.Warn("SingleInstance", $"无法联系已运行的 CCNest：{ex.Message}");
        }
    }

    public void StartListening()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(token);
                    if (line == null) continue;
                    var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(a => AllowedFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
                    ArgumentsReceived?.Invoke(args.Length == 0 ? new[] { "--show" } : args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Debug("SingleInstance", ex.Message);
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        }
        _mutex.Dispose();
    }
}
