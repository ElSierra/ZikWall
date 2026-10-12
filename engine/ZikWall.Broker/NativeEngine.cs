using System.ComponentModel;
using System.Runtime.InteropServices;
using ZikWall.WinUI.Models;

sealed class NativeEngine : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeRule
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Application;
        [MarshalAs(UnmanagedType.LPWStr)] public string Address;
        public uint Prefix, Protocol, Port, Direction, Block;
        public ulong Priority;
    }
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern uint ZikOpen(out IntPtr engine);
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern uint ZikApply(IntPtr engine, [In] NativeRule[] rules, uint count);
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern void ZikClose(IntPtr engine);
    private IntPtr handle;
    public NativeEngine()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeEngine).Assembly, (name, _, _) => name == "ZikWall.Engine.dll" ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, name)) : IntPtr.Zero);
    }
    private void EnsureOpen()
    {
        if (handle != IntPtr.Zero) return;
        uint status = ZikOpen(out handle);
        if (status != 0) throw new Win32Exception(unchecked((int)status), $"WFP session could not start (0x{status:X8}).");
    }
    public int Apply(IReadOnlyList<FirewallRule> rules, DateTimeOffset now)
    {
        EnsureOpen();
        var active = rules.Where(x => x.IsActive(now)).Select((rule, index) =>
        {
            rule.Validate();
            var (address, prefix) = FirewallRule.ParseNetwork(rule.RemoteNetwork);
            return new NativeRule { Name = rule.Name, Application = rule.Application, Address = address, Prefix = prefix, Protocol = (uint)rule.Protocol, Port = (uint)rule.RemotePort, Direction = (uint)rule.Direction, Block = rule.Block ? 1u : 0u, Priority = (ulong)(10001 - index) };
        }).ToArray();
        uint status = ZikApply(handle, active, (uint)active.Length);
        if (status != 0) throw new Win32Exception(unchecked((int)status), $"Windows rejected the policy (0x{status:X8}); the previous rules remain active.");
        return active.Length;
    }
    public void Dispose() { if (handle != IntPtr.Zero) { ZikClose(handle); handle = IntPtr.Zero; } }
}
