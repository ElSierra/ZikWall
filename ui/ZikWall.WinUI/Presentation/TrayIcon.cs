using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZikWall.WinUI.Presentation;

internal sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8000 + 73;
    private readonly nint window;
    private readonly SubclassProc callback;
    private readonly Action open, hide, pause, exit;
    public bool AllowClose { get; set; }
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIcon data;
    private bool disposed;
    public TrayIcon(nint hwnd, Action open, Action hide, Action pause, Action exit)
    {
        window = hwnd; this.open = open; this.hide = hide; this.pause = pause; this.exit = exit;
        callback = WindowProc;
        data = new() { Size = (uint)Marshal.SizeOf<NotifyIcon>(), Window = hwnd, Id = 1, Flags = 7, CallbackMessage = Callback,
            Icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "ZikWall.ico"), 1, 16, 16, 0x10), Tip = "ZikWall · network monitor", Info = "", InfoTitle = "" };
        if (data.Icon == 0 || !SetWindowSubclass(window, callback, 73, 0)) { if (data.Icon != 0) DestroyIcon(data.Icon); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!Shell_NotifyIcon(0, ref data)) { Dispose(); throw new Win32Exception("Could not create ZikWall's tray icon."); }
    }
    private nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint reference)
    {
        // WinUI Window.Close/WM_CLOSE does not consistently raise AppWindow.Closing.
        // Intercept the actual native X/Alt+F4 path before WinUI destroys the window.
        if (message == 0x10 && !AllowClose) { hide(); return 0; }
        if (message == taskbarCreated) Shell_NotifyIcon(0, ref data);
        if (message == Callback)
        {
            if ((uint)lparam is 0x203 or 0x202) open();
            if ((uint)lparam == 0x205)
            {
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "Open ZikWall"); AppendMenu(menu, 0, 2, "Pause / resume connection snapshots");
                    AppendMenu(menu, 0x800, 0, ""); AppendMenu(menu, 0, 3, "Exit ZikWall");
                    GetCursorPos(out var point); SetForegroundWindow(window);
                    int command = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, window, 0);
                    if (command == 1) open(); else if (command == 2) pause(); else if (command == 3) exit();
                    PostMessage(window, 0, 0, 0);
                }
                finally { DestroyMenu(menu); }
            }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Shell_NotifyIcon(2, ref data); RemoveWindowSubclass(window, callback, 73);
        if (data.Icon != 0) DestroyIcon(data.Icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIcon
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wparam, nint lparam, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint msg, nuint wp, nint lp);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint command, ref NotifyIcon data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nuint wp, nint lp);
}
