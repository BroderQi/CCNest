using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using static CCNest.Core.Wfp.WfpNative;

namespace CCNest.Core.Wfp;

public sealed class WfpException : Exception
{
    public WfpException(string operation, uint code) : base($"{operation} 失败（0x{code:X8}）{Hint(code)}") => Code = code;
    public uint Code { get; }

    private static string Hint(uint code) => code switch
    {
        5 or 0x80070005 => "：需要管理员权限",
        0x800706D9 => "：基本筛选引擎（BFE）服务未运行",
        _ => "",
    };
}

/// <summary>A condition in a WFP filter, described in managed terms.</summary>
public abstract record WfpCondition
{
    public sealed record AppId(string Path) : WfpCondition;
    public sealed record Loopback : WfpCondition;
    public sealed record RemotePort(ushort Port) : WfpCondition;
    public sealed record LocalPort(ushort Port) : WfpCondition;
    public sealed record Protocol(byte Number) : WfpCondition;
    public sealed record LocalInterface(ulong Luid) : WfpCondition;
    public sealed record RemoteSubnet(IPAddress Address, int PrefixLength) : WfpCondition;
}

public sealed record WfpFilterSpec(string Name, Guid Layer, bool Permit, byte Weight, IReadOnlyList<WfpCondition> Conditions);

/// <summary>
/// Thin managed wrapper over a WFP engine session. All CCNest objects share one provider and one
/// high-priority sublayer, so they can always be found and removed together, even after a crash.
/// </summary>
public sealed class WfpEngine : IDisposable
{
    public static readonly Guid ProviderKey = new("6a3c0d1e-6c2b-4a8e-9f0e-0c0e5e7c4a01");
    public static readonly Guid SubLayerKey = new("6a3c0d1e-6c2b-4a8e-9f0e-0c0e5e7c4a02");

    private IntPtr _engine;
    private readonly List<IntPtr> _allocations = new();
    private readonly List<IntPtr> _wfpAllocations = new();

    /// <param name="dynamic">Dynamic sessions remove every object when the process exits (or crashes).</param>
    public WfpEngine(bool dynamic)
    {
        var name = Marshal.StringToHGlobalUni("CCNest");
        try
        {
            var session = new FWPM_SESSION0
            {
                displayName = name,
                flags = dynamic ? FWPM_SESSION_FLAG_DYNAMIC : 0,
                txnWaitTimeoutInMSec = 10_000,
            };
            var rc = FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, IntPtr.Zero, ref session, out _engine);
            if (rc != 0) throw new WfpException("打开 WFP 引擎", rc);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    public void InTransaction(Action action)
    {
        Check(FwpmTransactionBegin0(_engine, 0), "开始 WFP 事务");
        try
        {
            action();
            Check(FwpmTransactionCommit0(_engine), "提交 WFP 事务");
        }
        catch
        {
            FwpmTransactionAbort0(_engine);
            throw;
        }
    }

    public void EnsureProviderAndSubLayer()
    {
        var provider = new FWPM_PROVIDER0
        {
            providerKey = ProviderKey,
            displayName = Str("CCNest"),
            displayDescription = Str("CCNest Claude environment protection"),
        };
        var rc = FwpmProviderAdd0(_engine, ref provider, IntPtr.Zero);
        if (rc != 0 && rc != FWP_E_ALREADY_EXISTS) throw new WfpException("添加 WFP 提供程序", rc);

        var providerKeyPtr = Alloc(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(ProviderKey, providerKeyPtr, false);
        var subLayer = new FWPM_SUBLAYER0
        {
            subLayerKey = SubLayerKey,
            displayName = Str("CCNest protection"),
            displayDescription = Str("CCNest kill switch / app enforcement"),
            providerKey = providerKeyPtr,
            weight = 0xFFFF,
        };
        rc = FwpmSubLayerAdd0(_engine, ref subLayer, IntPtr.Zero);
        if (rc != 0 && rc != FWP_E_ALREADY_EXISTS) throw new WfpException("添加 WFP 子层", rc);
    }

    public ulong AddFilter(WfpFilterSpec spec)
    {
        var conditions = spec.Conditions.Select(BuildCondition).ToArray();
        var condSize = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
        var condPtr = conditions.Length == 0 ? IntPtr.Zero : Alloc(condSize * conditions.Length);
        for (var i = 0; i < conditions.Length; i++)
            Marshal.StructureToPtr(conditions[i], condPtr + i * condSize, false);

        var providerKeyPtr = Alloc(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(ProviderKey, providerKeyPtr, false);

        var filter = new FWPM_FILTER0
        {
            filterKey = Guid.NewGuid(),
            displayName = Str("CCNest: " + spec.Name),
            displayDescription = Str("Added by CCNest"),
            providerKey = providerKeyPtr,
            layerKey = spec.Layer,
            subLayerKey = SubLayerKey,
            weightType = FWP_UINT8,
            weightValue = Math.Min(spec.Weight, (byte)15),
            numFilterConditions = (uint)conditions.Length,
            filterCondition = condPtr,
            actionType = spec.Permit ? FWP_ACTION_PERMIT : FWP_ACTION_BLOCK,
        };
        var rc = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out var id);
        if (rc != 0) throw new WfpException($"添加筛选器“{spec.Name}”", rc);
        return id;
    }

    /// <summary>Deletes every filter owned by the CCNest provider, then the sublayer and provider.</summary>
    public int RemoveAll()
    {
        var ids = EnumerateOwnFilterIds();
        foreach (var id in ids)
        {
            var rc = FwpmFilterDeleteById0(_engine, id);
            if (rc != 0 && rc != FWP_E_FILTER_NOT_FOUND) throw new WfpException("删除筛选器", rc);
        }
        var sub = SubLayerKey;
        var rc2 = FwpmSubLayerDeleteByKey0(_engine, ref sub);
        if (rc2 != 0 && rc2 != FWP_E_SUBLAYER_NOT_FOUND) throw new WfpException("删除子层", rc2);
        var prov = ProviderKey;
        var rc3 = FwpmProviderDeleteByKey0(_engine, ref prov);
        if (rc3 != 0 && rc3 != FWP_E_PROVIDER_NOT_FOUND && rc3 != 0x8032000A /* in use */) throw new WfpException("删除提供程序", rc3);
        return ids.Count;
    }

    public List<ulong> EnumerateOwnFilterIds()
    {
        var result = new List<ulong>();
        Check(FwpmFilterCreateEnumHandle0(_engine, IntPtr.Zero, out var handle), "枚举筛选器");
        try
        {
            while (true)
            {
                Check(FwpmFilterEnum0(_engine, handle, 256, out var entries, out var count), "枚举筛选器");
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var filterPtr = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        var f = Marshal.PtrToStructure<FWPM_FILTER0>(filterPtr);
                        if (f.providerKey == IntPtr.Zero) continue;
                        if (Marshal.PtrToStructure<Guid>(f.providerKey) == ProviderKey || f.subLayerKey == SubLayerKey)
                            result.Add(f.filterId);
                    }
                }
                finally
                {
                    if (entries != IntPtr.Zero) FwpmFreeMemory0(ref entries);
                }
                if (count < 256) break;
            }
        }
        finally
        {
            FwpmFilterDestroyEnumHandle0(_engine, handle);
        }
        return result;
    }

    private FWPM_FILTER_CONDITION0 BuildCondition(WfpCondition c)
    {
        switch (c)
        {
            case WfpCondition.AppId app:
            {
                var rc = FwpmGetAppIdFromFileName0(app.Path, out var blob);
                if (rc != 0) throw new WfpException($"获取程序标识（{app.Path}）", rc);
                _wfpAllocations.Add(blob);
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondAleAppId, matchType = FWP_MATCH_EQUAL, valueType = FWP_BYTE_BLOB_TYPE, value = (ulong)blob };
            }
            case WfpCondition.Loopback:
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondFlags, matchType = FWP_MATCH_FLAGS_ALL_SET, valueType = FWP_UINT32, value = FWP_CONDITION_FLAG_IS_LOOPBACK };
            case WfpCondition.RemotePort rp:
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondIpRemotePort, matchType = FWP_MATCH_EQUAL, valueType = FWP_UINT16, value = rp.Port };
            case WfpCondition.LocalPort lp:
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondIpLocalPort, matchType = FWP_MATCH_EQUAL, valueType = FWP_UINT16, value = lp.Port };
            case WfpCondition.Protocol p:
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondIpProtocol, matchType = FWP_MATCH_EQUAL, valueType = FWP_UINT8, value = p.Number };
            case WfpCondition.LocalInterface li:
            {
                var ptr = Alloc(8);
                Marshal.WriteInt64(ptr, (long)li.Luid);
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondIpLocalInterface, matchType = FWP_MATCH_EQUAL, valueType = FWP_UINT64, value = (ulong)ptr };
            }
            case WfpCondition.RemoteSubnet s when s.Address.AddressFamily == AddressFamily.InterNetwork:
            {
                var bytes = s.Address.GetAddressBytes();
                var addr = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                var mask = s.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - s.PrefixLength);
                var ptr = Alloc(8);
                Marshal.WriteInt32(ptr, 0, (int)addr);
                Marshal.WriteInt32(ptr, 4, (int)mask);
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondIpRemoteAddress, matchType = FWP_MATCH_EQUAL, valueType = FWP_V4_ADDR_MASK, value = (ulong)ptr };
            }
            case WfpCondition.RemoteSubnet s6:
            {
                var ptr = Alloc(17);
                Marshal.Copy(s6.Address.GetAddressBytes(), 0, ptr, 16);
                Marshal.WriteByte(ptr, 16, (byte)s6.PrefixLength);
                return new FWPM_FILTER_CONDITION0 { fieldKey = CondIpRemoteAddress, matchType = FWP_MATCH_EQUAL, valueType = FWP_V6_ADDR_MASK, value = (ulong)ptr };
            }
            default:
                throw new NotSupportedException(c.GetType().Name);
        }
    }

    public static ulong? LuidForAlias(string alias) => ConvertInterfaceAliasToLuid(alias, out var luid) == 0 ? luid : null;
    public static ulong? LuidForIndex(int index) => ConvertInterfaceIndexToLuid((uint)index, out var luid) == 0 ? luid : null;

    private IntPtr Str(string s)
    {
        var p = Marshal.StringToHGlobalUni(s);
        _allocations.Add(p);
        return p;
    }

    private IntPtr Alloc(int size)
    {
        var p = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        _allocations.Add(p);
        return p;
    }

    private static void Check(uint rc, string op)
    {
        if (rc != 0) throw new WfpException(op, rc);
    }

    public void Dispose()
    {
        if (_engine != IntPtr.Zero)
        {
            FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
        foreach (var p in _allocations) Marshal.FreeHGlobal(p);
        _allocations.Clear();
        for (var i = 0; i < _wfpAllocations.Count; i++)
        {
            var p = _wfpAllocations[i];
            FwpmFreeMemory0(ref p);
        }
        _wfpAllocations.Clear();
    }
}
