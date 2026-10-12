using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public static class TcpDirection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SynOptions { public byte ActiveOpen; public uint ReceivedMss, SentMss; }
    [DllImport("iphlpapi.dll", ExactSpelling = true)] private static extern uint GetPerTcpConnectionEStats(byte[] row, int type, IntPtr rw, uint rwVersion, uint rwSize, out SynOptions options, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] private static extern uint GetPerTcp6ConnectionEStats(byte[] row, int type, IntPtr rw, uint rwVersion, uint rwSize, out SynOptions options, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);
    public static string Read(ConnectionEntry entry)
    {
        if (!entry.Protocol.StartsWith("TCP", StringComparison.Ordinal)) return "Unknown";
        if (entry.RemotePort is not > 0) return "Listening";
        if (entry.State == "SynSent") return "Outgoing";
        if (!OperatingSystem.IsWindows()) return "Unknown";
        bool ipv6 = entry.Protocol == "TCPv6";
        var row = new byte[ipv6 ? 52 : 20];
        var local = IPAddress.Parse(entry.LocalAddress); var remote = IPAddress.Parse(entry.RemoteAddress);
        if (ipv6)
        {
            // MIB_TCP6ROW starts with State, unlike MIB_TCP6ROW_OWNER_PID.
            local.GetAddressBytes().CopyTo(row, 4); remote.GetAddressBytes().CopyTo(row, 28);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(20), (uint)local.ScopeId);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(44), (uint)remote.ScopeId);
        }
        else { local.GetAddressBytes().CopyTo(row, 4); remote.GetAddressBytes().CopyTo(row, 12); }
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(ipv6 ? 24 : 8), (ushort)entry.LocalPort);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(ipv6 ? 48 : 16), (ushort)entry.RemotePort.Value);
        // Read-only SYN options need no statistics enablement or firewall changes.
        SynOptions options;
        uint status = ipv6
            ? GetPerTcp6ConnectionEStats(row, 0, IntPtr.Zero, 0, 0, out options, 0, 12, IntPtr.Zero, 0, 0)
            : GetPerTcpConnectionEStats(row, 0, IntPtr.Zero, 0, 0, out options, 0, 12, IntPtr.Zero, 0, 0);
        return status == 0 ? options.ActiveOpen != 0 ? "Outgoing" : "Incoming" : "Unknown";
    }
}
