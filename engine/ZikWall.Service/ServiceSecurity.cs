using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ZikWall.WinUI.Services;

internal static class ServiceSecurity
{
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, IntPtr buffer, int length, out int needed);
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    public static NamedPipeServerStream CreateListener()
    {
        // Authenticated local users can connect, but cannot create a pipe instance
        // (FILE_CREATE_PIPE_INSTANCE is deliberately excluded). Authenticate below
        // checks the actual connecting token and executable before doing any work.
        const string sddl = "D:P(A;;GA;;;SY)(A;;0x12019b;;;AU)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // Overlapped, duplex, first instance, reject remote clients.
            var handle = CreateNamedPipe(@"\\.\pipe\" + ServiceProtocol.PipeName, 0x40080003, 8, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    public static Process Authenticate(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var process = Process.GetProcessById(checked((int)pid));
        try
        {
            if (process.SessionId == 0 || !string.Equals(process.MainModule?.FileName,
                Path.Combine(AppContext.BaseDirectory, "ZikWall.exe"), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Only the installed ZikWall app can request a session.");
            string owner = OwnerSid(process);
            bool authorized = false;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(true);
                // UAC filtered administrators retain the Administrators SID as a
                // deny-only group. Explicitly allow those accounts, not all users.
                authorized = identity?.User?.Value == owner && IsAdministratorAccount(identity.AccessToken);
            });
            if (!authorized) throw new UnauthorizedAccessException("An administrator Windows account is required.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static bool IsAdministratorAccount(SafeAccessTokenHandle token)
    {
        // WindowsIdentity.Groups can omit deny-only groups; inspect TOKEN_GROUPS
        // explicitly so a UAC-filtered administrator does not need elevation.
        GetTokenInformation(token, 2, IntPtr.Zero, 0, out int size);
        if (size <= 0 || size > 65536) throw new UnauthorizedAccessException("Invalid client token groups.");
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 2, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            int count = Marshal.ReadInt32(buffer), stride = Marshal.SizeOf<SidAndAttributes>();
            if (count < 0 || count > (size - IntPtr.Size) / stride) throw new UnauthorizedAccessException("Invalid client token groups.");
            for (int i = 0; i < count; i++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(buffer + IntPtr.Size + i * stride);
                if ((group.Attributes & 0x14) != 0 && new SecurityIdentifier(group.Sid).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string OwnerSid(Process process)
    {
        if (!OpenProcessToken(process.Handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new UnauthorizedAccessException("Client has no user identity.");
    }

    public static SafeFileHandle CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Brokers die with the service. Their fixed DNS controller may break away
        // so stdin EOF can execute its NetEvent cleanup even after a broker crash.
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x3000 } };
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        { int error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error); }
        return job;
    }

    public static void Assign(SafeFileHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static void Terminate(SafeFileHandle job) => TerminateJobObject(job, 0);
}
