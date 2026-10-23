using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace CCNest.Core.Native;

public sealed record TcpListener(IPAddress Address, int Port, int Pid);

/// <summary>P/Invoke helpers around iphlpapi.dll and process queries.</summary>
public static class IpHelper
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    public static IReadOnlyList<TcpListener> GetTcpListeners()
    {
        var result = new List<TcpListener>();
        result.AddRange(ReadTable(AF_INET));
        result.AddRange(ReadTable(AF_INET6));
        return result;
    }

    private static IEnumerable<TcpListener> ReadTable(int family)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) yield break;
        var buffer = Marshal.AllocHGlobal(size);
        var rows = new List<TcpListener>();
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) yield break;
            var count = Marshal.ReadInt32(buffer);
            var ptr = buffer + 4;
            if (family == AF_INET)
            {
                const int rowSize = 24; // MIB_TCPROW_OWNER_PID
                for (var i = 0; i < count; i++)
                {
                    var row = ptr + i * rowSize;
                    var addr = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                    var port = PortFromNetwork(Marshal.ReadInt32(row, 8));
                    var pid = Marshal.ReadInt32(row, 20);
                    rows.Add(new TcpListener(addr, port, pid));
                }
            }
            else
            {
                const int rowSize = 56; // MIB_TCP6ROW_OWNER_PID
                for (var i = 0; i < count; i++)
                {
                    var row = ptr + i * rowSize;
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    var port = PortFromNetwork(Marshal.ReadInt32(row, 20));
                    var pid = Marshal.ReadInt32(row, 52);
                    rows.Add(new TcpListener(new IPAddress(bytes), port, pid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        foreach (var r in rows) yield return r;
    }

    private static int PortFromNetwork(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);

    /// <summary>Returns the process image path, or null when access is denied (e.g. SYSTEM services from a standard process).</summary>
    public static string? GetProcessPath(int pid)
    {
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? GetProcessName(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Index of the interface Windows would use to reach the given address (null on failure).</summary>
    public static int? GetBestInterfaceIndex(IPAddress destination)
    {
        var sa = SockaddrFor(destination);
        var handle = GCHandle.Alloc(sa, GCHandleType.Pinned);
        try
        {
            return GetBestInterfaceEx(handle.AddrOfPinnedObject(), out var index) == 0 ? (int)index : null;
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] SockaddrFor(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = new byte[16];
            b[0] = AF_INET;
            ip.GetAddressBytes().CopyTo(b, 4);
            return b;
        }
        var s6 = new byte[28];
        BitConverter.GetBytes((ushort)AF_INET6).CopyTo(s6, 0);
        ip.GetAddressBytes().CopyTo(s6, 8);
        return s6;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterfaceEx(IntPtr pDestAddr, out uint pdwBestIfIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
