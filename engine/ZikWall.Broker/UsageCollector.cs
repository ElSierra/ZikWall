using System.ComponentModel;
using System.Runtime.InteropServices;
using ZikWall.WinUI.Models;

internal sealed class UsageCollector : IDisposable
{
    private readonly Guid session = Guid.NewGuid();
    private bool running;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Row
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string Path;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Package;
        public ulong Received, Sent;
    }
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint zw_usage_start();
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint zw_usage_read([Out] Row[] rows, uint capacity, out uint count, out ulong events, out ulong unattributed, out ulong rejected, out uint lost, out uint buffers);
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void zw_usage_stop();
    public void Start()
    {
        if (running) return;
        uint error = zw_usage_start(); if (error != 0) throw new Win32Exception((int)error);
        running = true;
    }
    public UsageSnapshot Read()
    {
        if (!running) throw new InvalidDataException("Usage collection is not enabled.");
        var rows = new Row[512];
        uint error = zw_usage_read(rows, 512, out uint count, out ulong events, out ulong unattributed, out ulong rejected, out uint lost, out uint buffers);
        if (error != 0) throw new Win32Exception((int)error);
        return new(session, DateTimeOffset.UtcNow, rows.Take((int)count).Select(x => new AppTraffic(x.Path, x.Package, x.Received, x.Sent)).ToList(), events, unattributed, rejected, lost, buffers);
    }
    public void Dispose() { if (running) zw_usage_stop(); running = false; }
}
