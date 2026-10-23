using System.Runtime.InteropServices;
using CCNest.Core.Wfp;

namespace CCNest.Tests;

/// <summary>
/// Guards the hand-written x64 P/Invoke layouts. Expected values were produced by compiling a C probe
/// (sizeof/offsetof) against the Windows 10.0.26100 SDK headers fwpmu.h / fwpmtypes.h with MSVC 14.44.
/// </summary>
public class NativeLayoutTests
{
    [Fact]
    public void WfpStructs_MatchNativeSdkLayout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(16, Marshal.SizeOf<WfpNative.FWP_BYTE_BLOB>());
        Assert.Equal(16, Marshal.SizeOf<WfpNative.FWP_VALUE0>());

        Assert.Equal(40, Marshal.SizeOf<WfpNative.FWPM_FILTER_CONDITION0>());
        Assert.Equal(16, Off<WfpNative.FWPM_FILTER_CONDITION0>(nameof(WfpNative.FWPM_FILTER_CONDITION0.matchType)));
        Assert.Equal(24, Off<WfpNative.FWPM_FILTER_CONDITION0>(nameof(WfpNative.FWPM_FILTER_CONDITION0.valueType)));

        Assert.Equal(200, Marshal.SizeOf<WfpNative.FWPM_FILTER0>());
        Assert.Equal(16, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.displayName)));
        Assert.Equal(32, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.flags)));
        Assert.Equal(40, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.providerKey)));
        Assert.Equal(48, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.providerDataSize)));
        Assert.Equal(64, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.layerKey)));
        Assert.Equal(80, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.subLayerKey)));
        Assert.Equal(96, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.weightType)));
        Assert.Equal(112, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.numFilterConditions)));
        Assert.Equal(120, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.filterCondition)));
        Assert.Equal(128, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.actionType)));
        Assert.Equal(152, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.rawContext)));
        Assert.Equal(168, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.reserved)));
        Assert.Equal(176, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.filterId)));
        Assert.Equal(184, Off<WfpNative.FWPM_FILTER0>(nameof(WfpNative.FWPM_FILTER0.effectiveWeightType)));

        Assert.Equal(72, Marshal.SizeOf<WfpNative.FWPM_SESSION0>());
        Assert.Equal(32, Off<WfpNative.FWPM_SESSION0>(nameof(WfpNative.FWPM_SESSION0.flags)));
        Assert.Equal(48, Off<WfpNative.FWPM_SESSION0>(nameof(WfpNative.FWPM_SESSION0.sid)));
        Assert.Equal(64, Off<WfpNative.FWPM_SESSION0>(nameof(WfpNative.FWPM_SESSION0.kernelMode)));

        Assert.Equal(72, Marshal.SizeOf<WfpNative.FWPM_SUBLAYER0>());
        Assert.Equal(40, Off<WfpNative.FWPM_SUBLAYER0>(nameof(WfpNative.FWPM_SUBLAYER0.providerKey)));
        Assert.Equal(48, Off<WfpNative.FWPM_SUBLAYER0>(nameof(WfpNative.FWPM_SUBLAYER0.providerDataSize)));
        Assert.Equal(64, Off<WfpNative.FWPM_SUBLAYER0>(nameof(WfpNative.FWPM_SUBLAYER0.weight)));

        Assert.Equal(64, Marshal.SizeOf<WfpNative.FWPM_PROVIDER0>());
        Assert.Equal(40, Off<WfpNative.FWPM_PROVIDER0>(nameof(WfpNative.FWPM_PROVIDER0.providerDataSize)));
        Assert.Equal(56, Off<WfpNative.FWPM_PROVIDER0>(nameof(WfpNative.FWPM_PROVIDER0.serviceName)));

        Assert.Equal(8, Marshal.SizeOf<WfpNative.FWP_V4_ADDR_AND_MASK>());
        Assert.Equal(0x1001u, WfpNative.FWP_ACTION_BLOCK);
        Assert.Equal(0x1002u, WfpNative.FWP_ACTION_PERMIT);
        Assert.Equal(6u, WfpNative.FWP_MATCH_FLAGS_ALL_SET);
        Assert.Equal(12u, WfpNative.FWP_BYTE_BLOB_TYPE);
        Assert.Equal(0x100u, WfpNative.FWP_V4_ADDR_MASK);
        Assert.Equal(0x101u, WfpNative.FWP_V6_ADDR_MASK);
    }

    private static int Off<T>(string field) => (int)Marshal.OffsetOf<T>(field);
}
