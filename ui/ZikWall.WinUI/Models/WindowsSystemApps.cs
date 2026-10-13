namespace ZikWall.WinUI.Models;

// Display/notification classification only, never a trust or firewall decision.
// Uses captured identity strings; no file inspection, package scans or network IO.
public sealed class WindowsSystemApps
{
    private readonly string[] roots;
    private readonly string windows;
    private static readonly HashSet<string> inboxPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsCalculator", "Microsoft.WindowsCamera", "Microsoft.WindowsNotepad",
        "Microsoft.WindowsSoundRecorder", "Microsoft.WindowsAlarms", "Microsoft.WindowsStore",
        "Microsoft.WindowsFeedbackHub", "Microsoft.Windows.Photos", "Microsoft.YourPhone",
        "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.SecHealthUI"
    };
    public WindowsSystemApps() : this(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)) { }
    internal WindowsSystemApps(string windows, string programFiles, string programData)
    {
        this.windows = Prefix(windows);
        var locations = new List<string>();
        if (this.windows.Length > 0)
            foreach (string folder in new[] { "System32", "SysWOW64", "SystemApps", "WinSxS", "servicing", "Microsoft.NET", "ImmersiveControlPanel" })
                locations.Add(this.windows + folder + "\\");
        if (Prefix(programFiles) is { Length: > 0 } programs) locations.Add(programs + "Windows Defender\\");
        if (Prefix(programData) is { Length: > 0 } data) locations.Add(data + "Microsoft\\Windows Defender\\Platform\\");
        roots = locations.ToArray();
    }
    private static string Prefix(string path) => path.Length == 0 ? "" : path.Replace('/', '\\').TrimEnd('\\') + "\\";
    public bool IsSystem(ConnectionEntry entry)
    {
        if (entry.ProcessId == 4 && entry.ProcessPath.Length == 0) return true; // Windows System PID.
        string path = entry.ProcessPath;
        if (path.Contains('/')) path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        // Windows supplies canonical paths. Ambiguous saved paths remain visible.
        if (!path.Contains(@"\..\", StringComparison.Ordinal) && !path.Contains(@"\.\", StringComparison.Ordinal))
        {
            foreach (string root in roots)
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            if (windows.Length > 0 && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                var name = path.AsSpan(windows.Length);
                if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("regedit.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("splwow64.exe", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        var family = entry.PackageFamily.AsSpan();
        int separator = family.LastIndexOf('_');
        if (separator < 1) return false;
        var publisher = family[(separator + 1)..];
        if (!publisher.Equals("cw5n1h2txyewy", StringComparison.OrdinalIgnoreCase)
            && !publisher.Equals("8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)) return false;
        var package = family[..separator];
        return package.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)
            || package.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
            || package.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)
            || inboxPackages.Contains(package.ToString());
    }
}
