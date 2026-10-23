using System.Net;
using CCNest.Core.Infrastructure;

namespace CCNest.Core.Wfp;

public enum KillSwitchMode
{
    /// <summary>Only the listed Claude programs are restricted: they may reach loopback (CCNest) and nothing else.</summary>
    AppScoped,
    /// <summary>Everything is blocked except loopback, the CCNest TUN interface, CCNest's core and the first-stage client.</summary>
    SystemWide,
    /// <summary>Crash state: everything is blocked except loopback, DHCP and (optionally) LAN.</summary>
    Lockdown,
}

public sealed record KillSwitchPlan
{
    public KillSwitchMode Mode { get; init; }
    public IReadOnlyList<string> AllowedAppPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopedAppPaths { get; init; } = Array.Empty<string>();
    public ulong? TunInterfaceLuid { get; init; }
    public bool AllowLan { get; init; } = true;
    /// <summary>Loopback ports of the first-stage client that protected programs must not use directly.</summary>
    public IReadOnlyList<int> BlockedLoopbackPorts { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Translates a <see cref="KillSwitchPlan"/> into WFP filters. Filters live in CCNest's own sublayer
/// (highest weight); within it, higher-weight filters are evaluated first. A block in CCNest's
/// sublayer overrides permits from other sublayers, so the rules hold even when Windows Defender
/// Firewall is disabled (only the BFE service is required).
/// </summary>
public static class KillSwitch
{
    private static readonly (string Cidr, bool V6)[] Lan =
    {
        ("10.0.0.0/8", false), ("172.16.0.0/12", false), ("192.168.0.0/16", false), ("169.254.0.0/16", false),
        ("224.0.0.0/4", false), ("255.255.255.255/32", false),
        ("fe80::/10", true), ("fc00::/7", true), ("ff00::/8", true),
    };

    public static IReadOnlyList<WfpFilterSpec> BuildFilters(KillSwitchPlan plan)
    {
        var list = new List<WfpFilterSpec>();
        switch (plan.Mode)
        {
            case KillSwitchMode.AppScoped:
                foreach (var app in plan.ScopedAppPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(app);
                    foreach (var (layer, v6) in ConnectLayers())
                    {
                        foreach (var port in plan.BlockedLoopbackPorts.Distinct())
                            list.Add(new WfpFilterSpec($"{name} 禁止直连一级端口 {port}{V(v6)}", layer, false, 15,
                                new WfpCondition[] { new WfpCondition.AppId(app), new WfpCondition.Loopback(), new WfpCondition.RemotePort((ushort)port) }));
                        list.Add(new WfpFilterSpec($"{name} 允许本机回环{V(v6)}", layer, true, 14,
                            new WfpCondition[] { new WfpCondition.AppId(app), new WfpCondition.Loopback() }));
                        list.Add(new WfpFilterSpec($"{name} 阻止直接联网{V(v6)}", layer, false, 1,
                            new WfpCondition[] { new WfpCondition.AppId(app) }));
                    }
                }
                break;

            case KillSwitchMode.SystemWide:
            case KillSwitchMode.Lockdown:
                var lockdown = plan.Mode == KillSwitchMode.Lockdown;
                foreach (var (layer, v6, inbound) in AllLayers())
                {
                    if (!lockdown)
                    {
                        foreach (var app in plan.AllowedAppPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                            list.Add(new WfpFilterSpec($"允许 {Path.GetFileName(app)}{V(v6)}{I(inbound)}", layer, true, 15,
                                new WfpCondition[] { new WfpCondition.AppId(app) }));
                        if (!inbound)
                            foreach (var port in plan.BlockedLoopbackPorts.Distinct())
                                list.Add(new WfpFilterSpec($"禁止其他程序直连一级端口 {port}{V(v6)}", layer, false, 14,
                                    new WfpCondition[] { new WfpCondition.Loopback(), new WfpCondition.RemotePort((ushort)port) }));
                        if (plan.TunInterfaceLuid is { } luid)
                            list.Add(new WfpFilterSpec($"允许 CCNest 虚拟网卡{V(v6)}{I(inbound)}", layer, true, 12,
                                new WfpCondition[] { new WfpCondition.LocalInterface(luid) }));
                    }

                    list.Add(new WfpFilterSpec($"允许本机回环{V(v6)}{I(inbound)}", layer, true, 13, new WfpCondition[] { new WfpCondition.Loopback() }));

                    // DHCP keeps the physical adapter's address alive.
                    if (!v6)
                        list.Add(new WfpFilterSpec($"允许 DHCP{I(inbound)}", layer, true, 11, new WfpCondition[]
                            { new WfpCondition.Protocol(17), inbound ? new WfpCondition.LocalPort(68) : new WfpCondition.RemotePort(67) }));
                    else
                        list.Add(new WfpFilterSpec($"允许 DHCPv6{I(inbound)}", layer, true, 11, new WfpCondition[]
                            { new WfpCondition.Protocol(17), inbound ? new WfpCondition.LocalPort(546) : new WfpCondition.RemotePort(547) }));

                    if (plan.AllowLan)
                        foreach (var (cidr, isV6) in Lan.Where(l => l.V6 == v6))
                        {
                            var (addr, prefix) = ParseCidr(cidr);
                            list.Add(new WfpFilterSpec($"允许局域网 {cidr}{I(inbound)}", layer, true, 10,
                                new WfpCondition[] { new WfpCondition.RemoteSubnet(addr, prefix) }));
                        }

                    list.Add(new WfpFilterSpec($"{(lockdown ? "锁定：" : "")}阻止其他所有流量{V(v6)}{I(inbound)}", layer, false, 0, Array.Empty<WfpCondition>()));
                }
                break;
        }
        return list;
    }

    /// <summary>
    /// Applies the plan atomically (replacing any previous CCNest filters). The returned engine must be
    /// kept open for dynamic sessions: closing it (or the process dying) removes the filters. For
    /// non-dynamic sessions the filters survive the process and are removed by <see cref="RemoveAll"/>
    /// or a reboot, so the returned engine can be disposed immediately.
    /// </summary>
    public static WfpEngine Apply(KillSwitchPlan plan, bool dynamicSession, out int count)
    {
        var filters = BuildFilters(plan);
        var engine = new WfpEngine(dynamicSession);
        var added = 0;
        try
        {
            engine.InTransaction(() =>
            {
                engine.RemoveAll();
                engine.EnsureProviderAndSubLayer();
                foreach (var f in filters)
                {
                    engine.AddFilter(f);
                    added++;
                }
            });
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        count = added;
        Log.Info("KillSwitch", $"已应用 {plan.Mode} 规则，共 {count} 条 WFP 筛选器（{(dynamicSession ? "随进程自动撤销" : "进程退出后保留，重启后失效")}）");
        return engine;
    }

    /// <summary>Removes all CCNest WFP objects. Safe to call when none exist.</summary>
    public static int RemoveAll()
    {
        using var engine = new WfpEngine(dynamic: false);
        var removed = 0;
        engine.InTransaction(() => removed = engine.RemoveAll());
        Log.Info("KillSwitch", $"已移除 {removed} 条 CCNest WFP 筛选器");
        return removed;
    }

    public static int CountActiveFilters()
    {
        using var engine = new WfpEngine(dynamic: true);
        return engine.EnumerateOwnFilterIds().Count;
    }

    private static IEnumerable<(Guid, bool)> ConnectLayers()
    {
        yield return (WfpNative.LayerAleAuthConnectV4, false);
        yield return (WfpNative.LayerAleAuthConnectV6, true);
    }

    private static IEnumerable<(Guid, bool, bool)> AllLayers()
    {
        yield return (WfpNative.LayerAleAuthConnectV4, false, false);
        yield return (WfpNative.LayerAleAuthConnectV6, true, false);
        yield return (WfpNative.LayerAleAuthRecvAcceptV4, false, true);
        yield return (WfpNative.LayerAleAuthRecvAcceptV6, true, true);
    }

    internal static (IPAddress, int) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        return (IPAddress.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string V(bool v6) => v6 ? " (IPv6)" : "";
    private static string I(bool inbound) => inbound ? " [入站]" : "";
}
