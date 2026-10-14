using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ZikWall.WinUI.Services;

internal static class ServiceSessionClient
{
    internal static bool IsInstalled => InstalledProcessId() is not null;
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Configuration
    { public uint Type, Start, Error; public IntPtr BinaryPath, Group; public uint Tag; public IntPtr Dependencies, Account, DisplayName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out Status status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    public static async Task<ServiceSessionResponse?> TryStartAsync(bool domainsOnly)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint? servicePid = InstalledProcessId();
        if (servicePid is null) return null; // Portable build: retain explicit UAC.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var pipe = await ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != servicePid)
                throw new IOException("The ZikWall service identity did not match Windows Service Control Manager.");
            await ServiceProtocol.WriteAsync(pipe, new ServiceSessionRequest(1, domainsOnly ? "domains" : "firewall"), timeout.Token);
            var response = await ServiceProtocol.ReadAsync<ServiceSessionResponse>(pipe, timeout.Token);
            // The service must not disconnect its reusable pipe until this reply
            // has been consumed, including replies that report authorization errors.
            await ServiceProtocol.WriteAsync(pipe, true, timeout.Token);
            if (!response.Success) throw new IOException(response.Message);
            if (!Guid.TryParseExact(response.PipeId, "N", out _) || response.ProcessId <= 0)
                throw new IOException("Invalid ZikWall service response.");
            return response;
        }
        catch (OperationCanceledException ex)
        { throw new IOException("ZikWall service did not respond. Repair ZikWall using setup or restart the service.", ex); }
    }

    internal static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // Specific read/write rights exclude FILE_CREATE_PIPE_INSTANCE. Generic
            // write includes that right and would defeat the listener's strict ACL.
            var handle = CreateFile(@"\\.\pipe\" + ServiceProtocol.PipeName, 0x12019b, 0, IntPtr.Zero, 3, 0x40110000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try { return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle); }
                catch { handle.Dispose(); throw; }
            }
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is not (2 or 231)) throw new Win32Exception(error);
            await Task.Delay(50, token);
        }
    }

    private static uint? InstalledProcessId()
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenService(manager, ServiceProtocol.Name, 5); // QUERY_CONFIG | QUERY_STATUS only
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1060) return null;
                throw new Win32Exception(error);
            }
            try
            {
                QueryServiceConfig(service, IntPtr.Zero, 0, out int needed);
                if (needed <= 0 || needed > 65536) throw new IOException("Cannot read ZikWall service configuration.");
                IntPtr memory = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!QueryServiceConfig(service, memory, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    string? binary = Marshal.PtrToStringUni(Marshal.PtrToStructure<Configuration>(memory).BinaryPath);
                    // A portable checkout must never ask the installed service to run its binaries.
                    string expected = Path.Combine(AppContext.BaseDirectory, "ZikWall.Service.exe");
                    if (!string.Equals(binary?.Trim().Trim('"'), expected, StringComparison.OrdinalIgnoreCase)) return null;
                }
                finally { Marshal.FreeHGlobal(memory); }
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Status>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (status.State != 4 || status.ProcessId == 0)
                    throw new IOException("ZikWall service is stopped. Repair ZikWall using setup or start ZikWall Background Service in Windows Services.");
                return status.ProcessId;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
}
