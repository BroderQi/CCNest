using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CCNest.Core.Native;

namespace CCNest.Core.SystemEnv;

public sealed record InterfaceInfo(
    string Name, string Description, NetworkInterfaceType Type, OperationalStatus Status, int Index,
    IReadOnlyList<IPAddress> IPv4, IReadOnlyList<IPAddress> IPv6Global, IReadOnlyList<IPAddress> Dns,
    IReadOnlyList<IPAddress> Gateways, bool LooksLikeTun);

public sealed record NetworkSnapshot(
    IReadOnlyList<InterfaceInfo> Interfaces,
    InterfaceInfo? DefaultRouteV4,
    InterfaceInfo? DefaultRouteV6,
    bool HasGlobalIPv6)
{
    public IEnumerable<InterfaceInfo> TunAdapters => Interfaces.Where(i => i.LooksLikeTun && i.Status == OperationalStatus.Up);
}

/// <summary>Read-only inspection of adapters, routes, DNS servers and TUN devices.</summary>
public static class NetworkInspector
{
    private static readonly string[] TunMarkers =
    {
        "wintun", "wireguard", "mihomo", "meta", "clash", "sing-box", "singbox", "tun", "tap-windows", "ccnest", "hiddify", "v2ray", "xray",
    };

    public static NetworkSnapshot Capture()
    {
        var list = new List<InterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            var index = -1;
            try { index = props.GetIPv4Properties()?.Index ?? -1; } catch { /* no v4 */ }
            if (index < 0)
            {
                try { index = props.GetIPv6Properties()?.Index ?? -1; } catch { /* no v6 */ }
            }

            var v4 = props.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            var v6 = props.UnicastAddresses.Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && IsGlobalV6(a)).ToList();
            var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
            var systemTunnel = text.Contains("teredo") || text.Contains("isatap") || text.Contains("6to4") || text.Contains("ip-https");
            var looksTun = !systemTunnel && (TunMarkers.Any(m => text.Contains(m)) || v4.Any(IsFakeIp));

            list.Add(new InterfaceInfo(nic.Name, nic.Description, nic.NetworkInterfaceType, nic.OperationalStatus, index, v4, v6,
                props.DnsAddresses.ToList(), props.GatewayAddresses.Select(g => g.Address).ToList(), looksTun));
        }

        InterfaceInfo? byIndex(int? idx) => idx is null ? null : list.FirstOrDefault(i => i.Index == idx);
        var v4Route = byIndex(IpHelper.GetBestInterfaceIndex(IPAddress.Parse("1.1.1.1")));
        var v6Route = byIndex(IpHelper.GetBestInterfaceIndex(IPAddress.Parse("2606:4700:4700::1111")));
        var hasV6 = list.Any(i => i.Status == OperationalStatus.Up && !i.LooksLikeTun && i.IPv6Global.Count > 0);
        return new NetworkSnapshot(list, v4Route, v6Route, hasV6);
    }

    public static bool IsGlobalV6(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6Multicast &&
        !IPAddress.IsLoopback(a) && !a.IsIPv6UniqueLocal && !a.IsIPv4MappedToIPv6 && !a.ToString().StartsWith("fd", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for addresses from the fake-IP benchmarking range used by Clash/mihomo (198.18.0.0/15).</summary>
    public static bool IsFakeIp(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 198 && (b[1] == 18 || b[1] == 19);
    }

    /// <summary>Resolves a host with the system resolver (to detect fake-IP / hijacked DNS).</summary>
    public static async Task<(IPAddress[] Addresses, string? Error)> SystemResolveAsync(string host, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return (await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<IPAddress>(), ex is OperationCanceledException ? "解析超时" : ex.Message);
        }
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
