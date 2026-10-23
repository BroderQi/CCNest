using CCNest.Core.Wfp;

namespace CCNest.Tests;

public class KillSwitchPlanTests
{
    [Fact]
    public void SystemWide_BlocksEverythingElse_OnAllFourLayers()
    {
        var filters = KillSwitch.BuildFilters(new KillSwitchPlan
        {
            Mode = KillSwitchMode.SystemWide,
            AllowedAppPaths = new[] { @"C:\ProgramData\CCNest\core\mihomo.exe" },
            TunInterfaceLuid = 0x1234,
            BlockedLoopbackPorts = new[] { 7897 },
            AllowLan = false,
        });

        var blockAll = filters.Where(f => !f.Permit && f.Conditions.Count == 0).ToList();
        Assert.Equal(4, blockAll.Count);
        Assert.All(blockAll, f => Assert.Equal(0, f.Weight));
        Assert.Contains(filters, f => f.Permit && f.Conditions.OfType<WfpCondition.LocalInterface>().Any(c => c.Luid == 0x1234));
        Assert.Contains(filters, f => f.Permit && f.Conditions.OfType<WfpCondition.AppId>().Any());
        Assert.DoesNotContain(filters, f => f.Conditions.OfType<WfpCondition.RemoteSubnet>().Any()); // LAN not allowed

        // Direct use of the first-stage port by other apps is blocked, above the loopback permit.
        var portBlock = filters.First(f => !f.Permit && f.Conditions.OfType<WfpCondition.RemotePort>().Any(p => p.Port == 7897));
        var loopPermit = filters.First(f => f.Permit && f.Conditions.Count == 1 && f.Conditions[0] is WfpCondition.Loopback);
        Assert.True(portBlock.Weight > loopPermit.Weight);
        var appPermit = filters.First(f => f.Permit && f.Conditions.OfType<WfpCondition.AppId>().Any());
        Assert.True(appPermit.Weight > portBlock.Weight);
    }

    [Fact]
    public void Lockdown_HasNoAppOrTunExceptions()
    {
        var filters = KillSwitch.BuildFilters(new KillSwitchPlan
        {
            Mode = KillSwitchMode.Lockdown,
            AllowedAppPaths = new[] { @"C:\x\clash.exe" },
            TunInterfaceLuid = 1,
            AllowLan = true,
        });
        Assert.DoesNotContain(filters, f => f.Conditions.OfType<WfpCondition.AppId>().Any());
        Assert.DoesNotContain(filters, f => f.Conditions.OfType<WfpCondition.LocalInterface>().Any());
        Assert.Contains(filters, f => f.Permit && f.Conditions.OfType<WfpCondition.RemoteSubnet>().Any());
        Assert.Equal(4, filters.Count(f => !f.Permit && f.Conditions.Count == 0));
    }

    [Fact]
    public void AppScoped_OnlyAffectsListedPrograms()
    {
        var filters = KillSwitch.BuildFilters(new KillSwitchPlan
        {
            Mode = KillSwitchMode.AppScoped,
            ScopedAppPaths = new[] { @"C:\Apps\claude.exe" },
            BlockedLoopbackPorts = new[] { 7897, 7898 },
        });
        Assert.All(filters, f => Assert.Contains(f.Conditions, c => c is WfpCondition.AppId { Path: @"C:\Apps\claude.exe" }));
        Assert.DoesNotContain(filters, f => f.Conditions.Count == 0);
        // v4 + v6: 2 port blocks + loopback permit + catch-all block each
        Assert.Equal(8, filters.Count);
        var block = filters.First(f => !f.Permit && f.Conditions.Count == 1);
        var permit = filters.First(f => f.Permit);
        Assert.True(permit.Weight > block.Weight);
    }

    [Fact]
    public void ParseCidr()
    {
        var (addr, prefix) = KillSwitch.ParseCidr("192.168.0.0/16");
        Assert.Equal("192.168.0.0", addr.ToString());
        Assert.Equal(16, prefix);
    }
}
