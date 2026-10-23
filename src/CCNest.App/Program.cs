using CCNest.Core.Elevation;
using CCNest.Core.Infrastructure;
using CCNest.Core.Session;
using CCNest.Core.Settings;

namespace CCNest.App;

/// <summary>
/// Single executable, several process roles:
///   (default)            the UI (standard user, single instance)
///   --helper ...         the elevated helper (started through UAC for TUN / WFP)
///   --watchdog ...       the crash watchdog for an active session (standard user)
///   --restore-network    elevated one-shot that removes leftover WFP rules
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (Has(args, "--helper")) return RunHelper(args);
            if (Has(args, "--watchdog")) return RunWatchdog(args);
            if (Has(args, "--restore-network")) return RunRestoreNetwork();
        }
        catch (Exception ex)
        {
            Log.Error("Program", "辅助进程异常退出", ex);
            return 1;
        }

        Log.ProcessTag = "ui";
        using var instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            instance.ForwardToFirstInstance(args);
            return 0;
        }

        Log.Prune();
        Log.Info("Program", $"CCNest {Core.AppInfo.Version} 启动（{Environment.OSVersion.VersionString}）");
        var app = new App { StartupArgs = args };
        app.InitializeComponent();
        instance.ArgumentsReceived += forwarded => app.Dispatcher.BeginInvoke(() => app.HandleArguments(forwarded, fromSecondInstance: true));
        instance.StartListening();
        return app.Run();
    }

    private static int RunHelper(string[] args)
    {
        Log.ProcessTag = "helper";
        if (!HelperClient.IsCurrentProcessElevated())
        {
            Log.Error("Program", "--helper 必须以管理员权限运行");
            return 5;
        }
        var parent = int.Parse(Value(args, "--parent") ?? throw new ArgumentException("--parent"));
        var pipe = Value(args, "--pipe") ?? throw new ArgumentException("--pipe");
        var sid = Value(args, "--client-sid") ?? throw new ArgumentException("--client-sid");
        var policy = Enum.TryParse<CrashPolicy>(Value(args, "--policy"), out var p) ? p : CrashPolicy.KeepBlocked;
        if (!pipe.StartsWith("ccnest-helper-", StringComparison.Ordinal) || pipe.Length > 120) throw new ArgumentException("pipe");
        return new HelperServer(parent, pipe, sid, policy).RunAsync().GetAwaiter().GetResult();
    }

    private static int RunWatchdog(string[] args)
    {
        var parent = int.Parse(Value(args, "--parent") ?? "0");
        var session = Value(args, "--session") ?? "";
        return RecoveryService.RunWatchdogAsync(parent, session).GetAwaiter().GetResult();
    }

    private static int RunRestoreNetwork()
    {
        Log.ProcessTag = "restore";
        if (!HelperClient.IsCurrentProcessElevated()) return 5;
        return RecoveryService.RemoveLeftoverFirewallRules();
    }

    internal static bool Has(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    internal static string? Value(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
