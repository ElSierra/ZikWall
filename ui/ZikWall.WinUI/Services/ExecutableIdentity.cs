using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZikWall.WinUI.Services;

public sealed record ExecutableIdentityResult(string Path, string Sha256, int TrustStatus)
{
    public string TrustDescription => TrustStatus == 0 ? "Signature accepted by Windows (offline check; revocation not checked)" : $"Signature not established as trusted (Windows status 0x{TrustStatus:X8})";
}
public static class ExecutableIdentity
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Union;
        public IntPtr File; public uint StateAction; public IntPtr State, Url; public uint Flags, UiContext; public IntPtr Signature;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    public static async Task<ExecutableIdentityResult> InspectAsync(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) throw new InvalidDataException("Choose a local executable path.");
        // One file handle supplies the hash and trust check. Do not execute the inspected file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)); stream.Position = 0;
        var file = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path, Handle = stream.SafeFileHandle.DangerousGetHandle() };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>()); Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Union = 1, File = pointer, StateAction = 1, Flags = 0x1000 };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            int status = await Task.Run(() => WinVerifyTrust(new IntPtr(-1), ref action, ref data));
            return new(path, hash, status);
        }
        finally
        {
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<FileInfo>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }
}
