using System.ComponentModel;
using System.Runtime.InteropServices;

internal static class WindowsServiceHost
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MainCallback(uint count, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ControlCallback(uint control, uint type, IntPtr data, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceEntry
    { public string? Name; public MainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ControlCallback handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

    public static void Run(string name, Func<CancellationToken, Task> run)
    {
        using var stop = new CancellationTokenSource();
        IntPtr handle = IntPtr.Zero;
        var gate = new object();
        var status = new ServiceStatus { Type = 0x10, State = 2, WaitHint = 10000 };
        void Report(uint state, uint exit = 0)
        {
            lock (gate)
            {
                status.State = state; status.Win32Exit = exit;
                status.Accepted = state == 4 ? 5u : 0; // STOP and SHUTDOWN
                status.Checkpoint = state is 2 or 3 ? 1u : 0;
                status.WaitHint = state is 2 or 3 ? 10000u : 0;
                SetServiceStatus(handle, ref status);
            }
        }
        ControlCallback control = (code, _, _, _) =>
        {
            if (code is 1 or 5) { Report(3); stop.Cancel(); }
            else if (code == 4) { lock (gate) SetServiceStatus(handle, ref status); }
            return 0;
        };
        MainCallback main = (_, _) =>
        {
            handle = RegisterServiceCtrlHandlerEx(name, control, IntPtr.Zero);
            if (handle == IntPtr.Zero) return;
            Report(2);
            uint error = 0;
            try
            {
                // RunAsync creates its protected listener synchronously before its first await.
                Task task = run(stop.Token);
                if (task.IsFaulted) task.GetAwaiter().GetResult();
                Report(4);
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception) { error = 1064; }
            finally { Report(1, error); }
        };
        if (!StartServiceCtrlDispatcher([new() { Name = name, Main = main }, new()]))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(main); GC.KeepAlive(control);
    }
}
