using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CCNest.Core.SystemEnv;

/// <summary>Complete WinINet proxy configuration of the default (LAN) connection.</summary>
public sealed record SystemProxyState
{
    public bool ProxyEnabled { get; init; }
    public string? ProxyServer { get; init; }
    public string? Bypass { get; init; }
    public bool AutoConfigEnabled { get; init; }
    public string? AutoConfigUrl { get; init; }
    public bool AutoDetect { get; init; }

    public bool Equivalent(SystemProxyState other) =>
        ProxyEnabled == other.ProxyEnabled &&
        (!ProxyEnabled || string.Equals(ProxyServer ?? "", other.ProxyServer ?? "", StringComparison.OrdinalIgnoreCase)) &&
        AutoConfigEnabled == other.AutoConfigEnabled &&
        (!AutoConfigEnabled || string.Equals(AutoConfigUrl ?? "", other.AutoConfigUrl ?? "", StringComparison.OrdinalIgnoreCase)) &&
        AutoDetect == other.AutoDetect;

    public string Describe()
    {
        var parts = new List<string>();
        if (ProxyEnabled) parts.Add($"手动代理 {ProxyServer}");
        if (AutoConfigEnabled) parts.Add($"PAC 脚本 {AutoConfigUrl}");
        if (AutoDetect) parts.Add("自动检测设置");
        return parts.Count == 0 ? "未使用代理（直连）" : string.Join("，", parts);
    }

    /// <summary>Extracts the loopback port of a manual proxy like "127.0.0.1:7897" or "http=127.0.0.1:7897;https=...".</summary>
    public int? LoopbackPort()
    {
        if (!ProxyEnabled || string.IsNullOrWhiteSpace(ProxyServer)) return null;
        foreach (var part in ProxyServer.Split(';'))
        {
            var v = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            v = v.Replace("http://", "", StringComparison.OrdinalIgnoreCase).Replace("socks=", "", StringComparison.OrdinalIgnoreCase);
            var colon = v.LastIndexOf(':');
            if (colon <= 0) continue;
            var host = v[..colon].Trim();
            if ((host is "127.0.0.1" or "localhost" or "[::1]" or "::1") && int.TryParse(v[(colon + 1)..], out var port)) return port;
        }
        return null;
    }
}

public interface ISystemProxyBackend
{
    SystemProxyState Read();
    void Write(SystemProxyState state);
}

/// <summary>
/// Reads and writes the Windows system proxy through the documented WinINet per-connection API
/// (the same mechanism as Settings › Proxy). Changes are broadcast with INTERNET_OPTION_SETTINGS_CHANGED
/// and INTERNET_OPTION_REFRESH so running Chromium/Electron/WinINet applications pick them up.
/// Limitation: dial-up/VPN connection entries keep their own proxy settings and are not modified.
/// </summary>
public sealed class WinInetProxyBackend : ISystemProxyBackend
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public SystemProxyState Read()
    {
        var options = new[]
        {
            new INTERNET_PER_CONN_OPTION { dwOption = INTERNET_PER_CONN_FLAGS },
            new INTERNET_PER_CONN_OPTION { dwOption = INTERNET_PER_CONN_PROXY_SERVER },
            new INTERNET_PER_CONN_OPTION { dwOption = INTERNET_PER_CONN_PROXY_BYPASS },
            new INTERNET_PER_CONN_OPTION { dwOption = INTERNET_PER_CONN_AUTOCONFIG_URL },
        };
        var optSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION>();
        var buffer = Marshal.AllocHGlobal(optSize * options.Length);
        try
        {
            for (var i = 0; i < options.Length; i++) Marshal.StructureToPtr(options[i], buffer + i * optSize, false);
            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>(),
                pszConnection = IntPtr.Zero,
                dwOptionCount = options.Length,
                pOptions = buffer,
            };
            var size = list.dwSize;
            if (!InternetQueryOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ref size))
                return ReadFromRegistry();

            var flags = Marshal.ReadInt32(buffer, 8);
            string? Str(int idx)
            {
                var p = Marshal.ReadIntPtr(buffer + idx * optSize, 8);
                if (p == IntPtr.Zero) return null;
                var s = Marshal.PtrToStringUni(p);
                GlobalFree(p);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            return new SystemProxyState
            {
                ProxyEnabled = (flags & PROXY_TYPE_PROXY) != 0,
                ProxyServer = Str(1),
                Bypass = Str(2),
                AutoConfigEnabled = (flags & PROXY_TYPE_AUTO_PROXY_URL) != 0,
                AutoConfigUrl = Str(3),
                AutoDetect = (flags & PROXY_TYPE_AUTO_DETECT) != 0,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SystemProxyState ReadFromRegistry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        var pac = k?.GetValue("AutoConfigURL") as string;
        return new SystemProxyState
        {
            ProxyEnabled = (k?.GetValue("ProxyEnable") as int? ?? 0) != 0,
            ProxyServer = k?.GetValue("ProxyServer") as string,
            Bypass = k?.GetValue("ProxyOverride") as string,
            AutoConfigEnabled = !string.IsNullOrEmpty(pac),
            AutoConfigUrl = pac,
        };
    }

    public void Write(SystemProxyState state)
    {
        var flags = PROXY_TYPE_DIRECT;
        if (state.ProxyEnabled) flags |= PROXY_TYPE_PROXY;
        if (state.AutoConfigEnabled) flags |= PROXY_TYPE_AUTO_PROXY_URL;
        if (state.AutoDetect) flags |= PROXY_TYPE_AUTO_DETECT;

        var strings = new List<IntPtr>();
        IntPtr S(string? s)
        {
            var p = Marshal.StringToHGlobalUni(s ?? "");
            strings.Add(p);
            return p;
        }

        var optSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION>();
        var buffer = Marshal.AllocHGlobal(optSize * 4);
        try
        {
            Marshal.WriteInt32(buffer, 0, INTERNET_PER_CONN_FLAGS);
            Marshal.WriteInt64(buffer, 8, flags);
            Marshal.WriteInt32(buffer + optSize, 0, INTERNET_PER_CONN_PROXY_SERVER);
            Marshal.WriteIntPtr(buffer + optSize, 8, S(state.ProxyServer));
            Marshal.WriteInt32(buffer + 2 * optSize, 0, INTERNET_PER_CONN_PROXY_BYPASS);
            Marshal.WriteIntPtr(buffer + 2 * optSize, 8, S(state.Bypass));
            Marshal.WriteInt32(buffer + 3 * optSize, 0, INTERNET_PER_CONN_AUTOCONFIG_URL);
            Marshal.WriteIntPtr(buffer + 3 * optSize, 8, S(state.AutoConfigUrl));

            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>(),
                pszConnection = IntPtr.Zero,
                dwOptionCount = 4,
                pOptions = buffer,
            };
            if (!InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, list.dwSize))
                throw new InvalidOperationException($"InternetSetOption 失败：{Marshal.GetLastWin32Error()}");
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            foreach (var p in strings) Marshal.FreeHGlobal(p);
        }
    }

    private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;
    private const int INTERNET_PER_CONN_FLAGS = 1;
    private const int INTERNET_PER_CONN_PROXY_SERVER = 2;
    private const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
    private const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;
    private const int PROXY_TYPE_DIRECT = 0x1;
    private const int PROXY_TYPE_PROXY = 0x2;
    private const int PROXY_TYPE_AUTO_PROXY_URL = 0x4;
    private const int PROXY_TYPE_AUTO_DETECT = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION
    {
        public int dwOption;
        public long Value; // union of DWORD / LPWSTR / FILETIME (8 bytes, 8-aligned on x64)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION_LIST
    {
        public int dwSize;
        public IntPtr pszConnection;
        public int dwOptionCount;
        public int dwOptionError;
        public IntPtr pOptions;
    }

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "InternetSetOptionW")]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, ref INTERNET_PER_CONN_OPTION_LIST buffer, int length);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "InternetSetOptionW")]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "InternetQueryOptionW")]
    private static extern bool InternetQueryOption(IntPtr hInternet, int option, ref INTERNET_PER_CONN_OPTION_LIST buffer, ref int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

public static class SystemProxy
{
    public static ISystemProxyBackend Backend { get; set; } = new WinInetProxyBackend();

    /// <summary>LAN destinations that should never be sent to the proxy.</summary>
    public const string DefaultBypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    public static SystemProxyState For(int port) => new()
    {
        ProxyEnabled = true,
        ProxyServer = $"127.0.0.1:{port}",
        Bypass = DefaultBypass,
        AutoConfigEnabled = false,
        AutoDetect = false,
    };

    /// <summary>WinHTTP machine proxy (used by some services); read-only diagnostic.</summary>
    public static async Task<string?> ReadWinHttpProxyAsync()
    {
        var r = await Infrastructure.ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            new[] { "winhttp", "show", "proxy" }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!r.Success) return null;
        var text = r.StdOut;
        if (text.Contains("Direct access", StringComparison.OrdinalIgnoreCase) || text.Contains("直接访问")) return null;
        return text.Trim();
    }
}
