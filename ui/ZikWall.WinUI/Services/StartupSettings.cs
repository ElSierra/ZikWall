using Microsoft.Win32;

namespace ZikWall.WinUI.Services;

public static class StartupSettings
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled => OperatingSystem.IsWindows() && Registry.CurrentUser.OpenSubKey(Run) is { } key && Read(key);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool Read(RegistryKey key) { using (key) return key.GetValue("ZikWall") is string value && value == Command; }
    private static string Command => $"\"{Environment.ProcessPath}\" --tray";
    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(Run);
        if (enabled) key.SetValue("ZikWall", Command, RegistryValueKind.String); else key.DeleteValue("ZikWall", false);
    }
}
