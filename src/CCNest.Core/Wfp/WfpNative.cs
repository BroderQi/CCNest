using System.Runtime.InteropServices;

namespace CCNest.Core.Wfp;

/// <summary>
/// Windows Filtering Platform P/Invoke definitions (x64 layouts, verified against fwpmtypes.h /
/// fwptypes.h from the Windows 10.0.26100 SDK). Explicit layouts are used where the native structs
/// contain unions with 8-byte alignment.
/// </summary>
internal static class WfpNative
{
    // Layers
    public static readonly Guid LayerAleAuthConnectV4 = new(0xc38d57d1, 0x05a7, 0x4c33, 0x90, 0x4f, 0x7f, 0xbc, 0xee, 0xe6, 0x0e, 0x82);
    public static readonly Guid LayerAleAuthConnectV6 = new(0x4a72393b, 0x319f, 0x44bc, 0x84, 0xc3, 0xba, 0x54, 0xdc, 0xb3, 0xb6, 0xb4);
    public static readonly Guid LayerAleAuthRecvAcceptV4 = new(0xe1cd9fe7, 0xf4b5, 0x4273, 0x96, 0xc0, 0x59, 0x2e, 0x48, 0x7b, 0x86, 0x50);
    public static readonly Guid LayerAleAuthRecvAcceptV6 = new(0xa3b42c97, 0x9f04, 0x4672, 0xb8, 0x7e, 0xce, 0xe9, 0xc4, 0x83, 0x25, 0x7f);

    // Conditions
    public static readonly Guid CondAleAppId = new(0xd78e1e87, 0x8644, 0x4ea5, 0x94, 0x37, 0xd8, 0x09, 0xec, 0xef, 0xc9, 0x71);
    public static readonly Guid CondIpRemoteAddress = new(0xb235ae9a, 0x1d64, 0x49b8, 0xa4, 0x4c, 0x5f, 0xf3, 0xd9, 0x09, 0x50, 0x45);
    public static readonly Guid CondIpRemotePort = new(0xc35a604d, 0xd22b, 0x4e1a, 0x91, 0xb4, 0x68, 0xf6, 0x74, 0xee, 0x67, 0x4b);
    public static readonly Guid CondIpLocalPort = new(0x0c1ba1af, 0x5765, 0x453f, 0xaf, 0x22, 0xa8, 0xf7, 0x91, 0xac, 0x77, 0x5b);
    public static readonly Guid CondIpProtocol = new(0x3971ef2b, 0x623e, 0x4f9a, 0x8c, 0xb1, 0x6e, 0x79, 0xb8, 0x06, 0xb9, 0xa7);
    public static readonly Guid CondFlags = new(0x632ce23b, 0x5167, 0x435c, 0x86, 0xd7, 0xe9, 0x03, 0x68, 0x4a, 0xa8, 0x0c);
    public static readonly Guid CondIpLocalInterface = new(0x4cd62a49, 0x59c3, 0x4969, 0xb7, 0xf3, 0xbd, 0xa5, 0xd3, 0x28, 0x90, 0xa4);

    // Data types
    public const uint FWP_EMPTY = 0;
    public const uint FWP_UINT8 = 1;
    public const uint FWP_UINT16 = 2;
    public const uint FWP_UINT32 = 3;
    public const uint FWP_UINT64 = 4;
    public const uint FWP_BYTE_BLOB_TYPE = 12;
    public const uint FWP_V4_ADDR_MASK = 0x100;
    public const uint FWP_V6_ADDR_MASK = 0x101;

    // Match types
    public const uint FWP_MATCH_EQUAL = 0;
    public const uint FWP_MATCH_FLAGS_ALL_SET = 6;

    // Actions
    public const uint FWP_ACTION_BLOCK = 0x1001;
    public const uint FWP_ACTION_PERMIT = 0x1002;

    public const uint FWP_CONDITION_FLAG_IS_LOOPBACK = 0x1;
    public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x1;
    public const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;

    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;
    public const uint FWP_E_PROVIDER_NOT_FOUND = 0x80320005;
    public const uint FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;
    public const uint FWP_E_FILTER_NOT_FOUND = 0x80320003;

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FWPM_DISPLAY_DATA0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct FWP_VALUE0
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ulong value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct FWPM_FILTER_CONDITION0
    {
        [FieldOffset(0)] public Guid fieldKey;
        [FieldOffset(16)] public uint matchType;
        [FieldOffset(24)] public uint valueType;
        [FieldOffset(32)] public ulong value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    public struct FWPM_FILTER0
    {
        [FieldOffset(0)] public Guid filterKey;
        [FieldOffset(16)] public IntPtr displayName;
        [FieldOffset(24)] public IntPtr displayDescription;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public IntPtr providerKey;
        [FieldOffset(48)] public uint providerDataSize;
        [FieldOffset(56)] public IntPtr providerData;
        [FieldOffset(64)] public Guid layerKey;
        [FieldOffset(80)] public Guid subLayerKey;
        [FieldOffset(96)] public uint weightType;
        [FieldOffset(104)] public ulong weightValue;
        [FieldOffset(112)] public uint numFilterConditions;
        [FieldOffset(120)] public IntPtr filterCondition;
        [FieldOffset(128)] public uint actionType;
        [FieldOffset(132)] public Guid actionFilterType;
        [FieldOffset(152)] public ulong rawContext;
        [FieldOffset(168)] public IntPtr reserved;
        [FieldOffset(176)] public ulong filterId;
        [FieldOffset(184)] public uint effectiveWeightType;
        [FieldOffset(192)] public ulong effectiveWeightValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct FWPM_SESSION0
    {
        [FieldOffset(0)] public Guid sessionKey;
        [FieldOffset(16)] public IntPtr displayName;
        [FieldOffset(24)] public IntPtr displayDescription;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(36)] public uint txnWaitTimeoutInMSec;
        [FieldOffset(40)] public uint processId;
        [FieldOffset(48)] public IntPtr sid;
        [FieldOffset(56)] public IntPtr username;
        [FieldOffset(64)] public int kernelMode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct FWPM_SUBLAYER0
    {
        [FieldOffset(0)] public Guid subLayerKey;
        [FieldOffset(16)] public IntPtr displayName;
        [FieldOffset(24)] public IntPtr displayDescription;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public IntPtr providerKey;
        [FieldOffset(48)] public uint providerDataSize;
        [FieldOffset(56)] public IntPtr providerData;
        [FieldOffset(64)] public ushort weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct FWPM_PROVIDER0
    {
        [FieldOffset(0)] public Guid providerKey;
        [FieldOffset(16)] public IntPtr displayName;
        [FieldOffset(24)] public IntPtr displayDescription;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public uint providerDataSize;
        [FieldOffset(48)] public IntPtr providerData;
        [FieldOffset(56)] public IntPtr serviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_V4_ADDR_AND_MASK
    {
        public uint addr; // host byte order
        public uint mask;
    }

    [StructLayout(LayoutKind.Sequential, Size = 17)]
    public struct FWP_V6_ADDR_AND_MASK
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] addr;
        public byte prefixLength;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    public static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, ref FWPM_SESSION0 session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmProviderAdd0(IntPtr engineHandle, ref FWPM_PROVIDER0 provider, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmProviderDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterCreateEnumHandle0(IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterEnum0(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern void FwpmFreeMemory0(ref IntPtr p);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    public static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint ConvertInterfaceAliasToLuid(string alias, out ulong luid);

    [DllImport("iphlpapi.dll")]
    public static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
}
