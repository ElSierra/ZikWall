using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

// No service installation or arbitrary command/file execution endpoint. One UI owns one session.
if (args.Length != 3 || !Guid.TryParseExact(args[0], "N", out var pipeId) || !int.TryParse(args[1], out int parentId) || !long.TryParse(args[2], out long startTicks)) return;
try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
    using var parent = Process.GetProcessById(parentId);
    if (parent.StartTime.ToUniversalTime().Ticks != startTicks || !string.Equals(parent.MainModule?.FileName, Path.Combine(AppContext.BaseDirectory, "ZikWall.exe"), StringComparison.OrdinalIgnoreCase)) return;
    string sid = PipeSecurityNative.OwnerSid(parent);
    using var pipe = PipeSecurityNative.Create("ZikWall." + pipeId.ToString("N"), sid);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await pipe.WaitForConnectionAsync(timeout.Token);
    if (!PipeSecurityNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId) || clientId != parentId) return;
    using var engine = new NativeEngine();
    using var usage = new UsageCollector();
    List<FirewallRule> rules = [];
    int active = 0;
    DateTimeOffset lastRequest = DateTimeOffset.UtcNow;
    var read = BrokerWire.ReadAsync<BrokerRequest>(pipe, CancellationToken.None);
    while (!parent.HasExited && DateTimeOffset.UtcNow - lastRequest < TimeSpan.FromSeconds(20))
    {
        if (await Task.WhenAny(read, Task.Delay(500)) != read)
        {
            if (rules.Count(x => x.IsActive(DateTimeOffset.UtcNow)) != active)
            {
                rules = rules.Where(x => x.IsActive(DateTimeOffset.UtcNow)).ToList();
                active = engine.Apply(rules, DateTimeOffset.UtcNow);
            }
            continue;
        }
        var request = await read;
        lastRequest = DateTimeOffset.UtcNow;
        BrokerResponse response;
        try
        {
            if (request.Command == "stop")
            {
                usage.Dispose();
                engine.Dispose();
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await BrokerWire.WriteAsync(pipe, new BrokerResponse(true, "Session filters and usage collection stopped"), stopTimeout.Token);
                break;
            }
            if (request.Command == "apply")
            {
                var next = request.Rules ?? throw new InvalidDataException("Missing rules.");
                var profileId = Guid.NewGuid();
                new PolicyDocument(1, profileId, false, [new(profileId, "IPC", next)]).Validate();
                active = engine.Apply(next, DateTimeOffset.UtcNow);
                rules = next;
            }
            else if (request.Command == "usage-start") usage.Start();
            else if (request.Command != "usage" && request.Command != "status") throw new InvalidDataException("Unsupported broker request.");
            response = request.Command is "usage" or "usage-start" ? new(true, "Observed traffic collection active", active, usage.Read()) : new(true, "Filtering session active", active);
        }
        catch (Exception ex) when (ex is InvalidDataException or Win32Exception) { response = new(false, ex.Message, active); }
        using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await BrokerWire.WriteAsync(pipe, response, writeTimeout.Token);
        read = BrokerWire.ReadAsync<BrokerRequest>(pipe, CancellationToken.None);
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
{
    // Closing the dynamic WFP session rolls back every filter, including on IPC failure.
    // The UI detects the broken pipe; never write a privileged log to a caller-supplied path.
}

static class PipeSecurityNative
{
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    public static string OwnerSid(Process process)
    {
        if (!OpenProcessToken(process.Handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value ?? throw new InvalidDataException("Parent has no user SID.");
    }
    public static NamedPipeServerStream Create(string name, string sid)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{sid})(A;;GA;;;SY)S:(ML;;NW;;;ME)", 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080003, 8, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
}
