using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// The owner-PID tables do not require elevation and never modify networking settings.
public static class ConnectionMonitor
{
    private const int MaxTableBytes = 32 * 1024 * 1024;
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, [Out] char[]? family);

    public static IReadOnlyList<ConnectionEntry> Capture()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The connection monitor requires Windows.");
        var rows = new List<ConnectionEntry>();
        foreach (bool tcp in new[] { true, false })
            foreach (bool ipv6 in new[] { false, true })
                rows.AddRange(Parse(ReadTable(tcp, ipv6), tcp, ipv6));
        var processes = new Dictionary<int, (string Name, string Path, string Package, long Started)>();
        Dictionary<int, string>? fallbackNames = null;
        var pathBuffer = new char[32768];
        foreach (int pid in rows.Select(x => x.ProcessId).Distinct())
        {
            string name = pid == 0 ? ConnectionContinuity.Unowned : $"PID {pid}", path = "";
            // Limited-information query avoids expensive MainModule enumeration. Do not
            // cache identity by PID: Windows can reuse a PID for a different executable.
            using var handle = OpenProcess(0x1000, false, pid);
            int length = pathBuffer.Length;
            if (!handle.IsInvalid && QueryFullProcessImageName(handle, 0, pathBuffer, ref length))
            {
                path = new string(pathBuffer, 0, length);
                name = Path.GetFileNameWithoutExtension(path);
            }
            else if (pid != 0)
            {
                // Process.ProcessName can enumerate system process information for
                // every denied PID. Take one name-only snapshot for this capture.
                fallbackNames ??= ReadProcessNames();
                if (fallbackNames.TryGetValue(pid, out string? fallback)) name = fallback;
            }
            string package = ""; long started = 0;
            if (!handle.IsInvalid)
            {
                GetProcessTimes(handle, out started, out _, out _, out _);
                uint packageLength = 0;
                if (GetPackageFamilyName(handle, ref packageLength, null) == 122 && packageLength <= 512)
                {
                    var packageName = new char[packageLength];
                    if (GetPackageFamilyName(handle, ref packageLength, packageName) == 0) package = new string(packageName).TrimEnd('\0');
                }
            }
            processes[pid] = (name, path, package, started);
        }
        return rows.Select(x => x with { ProcessName = processes[x.ProcessId].Name, ProcessPath = processes[x.ProcessId].Path, PackageFamily = processes[x.ProcessId].Package, ProcessStarted = processes[x.ProcessId].Started }).ToArray();
    }

    private static Dictionary<int, string> ReadProcessNames()
    {
        var result = new Dictionary<int, string>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return result;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (!Process32First(snapshot, ref entry)) return result;
        do { if (entry.ProcessId <= int.MaxValue) result[(int)entry.ProcessId] = Path.GetFileNameWithoutExtension(entry.Executable); }
        while (Process32Next(snapshot, ref entry));
        return result;
    }

    private static byte[] ReadTable(bool tcp, bool ipv6)
    {
        int size = 0;
        // The UI already sorts by app/address; asking Windows to sort first is wasted work.
        uint Call(IntPtr p) => tcp ? GetExtendedTcpTable(p, ref size, false, ipv6 ? 23 : 2, 5, 0)
            : GetExtendedUdpTable(p, ref size, false, ipv6 ? 23 : 2, 1, 0);
        uint result = Call(IntPtr.Zero);
        if (result != 122 && result != 0) throw new Win32Exception((int)result);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (size < 4 || size > MaxTableBytes) throw new InvalidDataException("Invalid Windows endpoint table size.");
            int allocated = size;
            var buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                result = Call(buffer);
                if (result == 122) continue; // Connections may change between the size query and read.
                if (result != 0) throw new Win32Exception((int)result);
                if (size < 4 || size > allocated) throw new InvalidDataException("Invalid Windows endpoint table length.");
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return bytes;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("The endpoint tables changed too quickly. Try refreshing again.");
    }

    // Kept separate from P/Invoke so malformed buffers and byte order can be tested.
    public static IReadOnlyList<ConnectionEntry> Parse(byte[] bytes, bool tcp, bool ipv6)
    {
        int rowSize = (tcp, ipv6) switch { (true, false) => 24, (true, true) => 56, (false, false) => 12, _ => 28 };
        if (bytes.Length < 4) throw new InvalidDataException("Truncated endpoint table.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > (bytes.Length - 4) / rowSize) throw new InvalidDataException("Truncated endpoint row.");
        var rows = new List<ConnectionEntry>((int)count);
        for (int i = 0; i < count; i++)
        {
            var row = bytes.AsSpan(4 + i * rowSize, rowSize);
            int localOffset = tcp && !ipv6 ? 4 : 0;
            string local = ipv6 ? new IPAddress(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..])).ToString()
                : new IPAddress(row.Slice(localOffset, 4)).ToString();
            int localPortOffset = ipv6 ? 20 : localOffset + 4;
            int port = BinaryPrimitives.ReadUInt16BigEndian(row[localPortOffset..]);
            string remote = "";
            int? remotePort = null;
            string state = "Local endpoint";
            if (tcp)
            {
                remote = ipv6 ? new IPAddress(row.Slice(24, 16), BinaryPrimitives.ReadUInt32LittleEndian(row[40..])).ToString()
                    : new IPAddress(row.Slice(12, 4)).ToString();
                remotePort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv6 ? 44 : 16)..]);
                state = ((TcpState)BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? 48 : 0)..])).ToString();
            }
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(row[(rowSize - 4)..]);
            rows.Add(new((tcp ? "TCP" : "UDP") + (ipv6 ? "v6" : "v4"), local, port, remote, remotePort, checked((int)pid), state));
        }
        return rows;
    }

    public static IReadOnlyList<AdapterCounters> CaptureTraffic()
    {
        var result = new List<AdapterCounters>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var stats = adapter.GetIPStatistics();
                result.Add(new(adapter.Id, adapter.Name, stats.BytesReceived, stats.BytesSent));
            }
            catch (NetworkInformationException) { }
        }
        return result;
    }
}
