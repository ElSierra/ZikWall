namespace ZikWall.WinUI.Models;

// Readable names for Microsoft and Windows executables whose file names are cryptic (svchost, MsMpEng, ...).
// A name is only applied when the executable lives in a Microsoft location, or when Windows hides the path
// (protected system processes), so a look-alike svchost.exe in Downloads keeps its raw file name.
public static class KnownApps
{
    private static readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "Windows kernel (System)", ["Registry"] = "Windows Registry", ["Secure System"] = "Windows Secure System",
        ["svchost"] = "Windows Service Host", ["services"] = "Windows Services", ["lsass"] = "Local Security Authority",
        ["lsaiso"] = "Credential Guard", ["wininit"] = "Windows Start-Up", ["winlogon"] = "Windows Logon", ["csrss"] = "Client Server Runtime",
        ["smss"] = "Session Manager", ["spoolsv"] = "Print Spooler", ["explorer"] = "File Explorer", ["dwm"] = "Desktop Window Manager",
        ["SearchHost"] = "Windows Search", ["SearchApp"] = "Windows Search", ["SearchIndexer"] = "Windows Search Indexer",
        ["SearchProtocolHost"] = "Windows Search Indexer", ["SearchFilterHost"] = "Windows Search Indexer",
        ["StartMenuExperienceHost"] = "Start menu", ["ShellExperienceHost"] = "Windows Shell Experience", ["ShellHost"] = "Windows Shell",
        ["sihost"] = "Shell Infrastructure Host", ["TextInputHost"] = "Windows Input Experience", ["ctfmon"] = "Text Input (CTF Loader)",
        ["LockApp"] = "Lock screen", ["RuntimeBroker"] = "Runtime Broker", ["backgroundTaskHost"] = "Background Task Host",
        ["taskhostw"] = "Windows Task Host", ["dllhost"] = "COM Surrogate", ["WmiPrvSE"] = "WMI Provider Host",
        ["ApplicationFrameHost"] = "Application Frame Host", ["SystemSettings"] = "Settings", ["CompPkgSrv"] = "Component Package Support",
        ["fontdrvhost"] = "Font Driver Host", ["audiodg"] = "Windows Audio", ["conhost"] = "Console Window Host",
        ["WUDFHost"] = "Windows Driver Host", ["dasHost"] = "Device Association Service", ["wlanext"] = "Wi-Fi Extensibility",
        ["CrossDeviceService"] = "Cross Device Experience", ["CrossDeviceResume"] = "Cross Device Resume",
        ["TiWorker"] = "Windows Modules Installer", ["TrustedInstaller"] = "Windows Modules Installer",
        ["MoUsoCoreWorker"] = "Windows Update", ["usocoreworker"] = "Windows Update", ["wuauclt"] = "Windows Update", ["UsoClient"] = "Windows Update",
        ["MsMpEng"] = "Microsoft Defender Antivirus", ["NisSrv"] = "Microsoft Defender Network Inspection",
        ["MpDefenderCoreService"] = "Microsoft Defender Core Service", ["MsSense"] = "Microsoft Defender for Endpoint",
        ["SecurityHealthService"] = "Windows Security", ["SecHealthUI"] = "Windows Security", ["smartscreen"] = "Windows SmartScreen",
        ["SgrmBroker"] = "System Guard Runtime Monitor", ["WidgetService"] = "Widgets", ["Widgets"] = "Widgets",
        ["PhoneExperienceHost"] = "Phone Link", ["YourPhone"] = "Phone Link", ["GameBar"] = "Xbox Game Bar", ["XboxPcApp"] = "Xbox",
        ["WinStore.App"] = "Microsoft Store", ["msedge"] = "Microsoft Edge", ["msedgewebview2"] = "Microsoft Edge WebView2",
        ["MicrosoftEdgeUpdate"] = "Microsoft Edge Update", ["OneDrive"] = "OneDrive", ["OneDrive.Sync.Service"] = "OneDrive Sync Service",
        ["FileCoAuth"] = "OneDrive co-authoring", ["Microsoft.SharePoint"] = "OneDrive (SharePoint sync)",
        ["ms-teams"] = "Microsoft Teams", ["msteams"] = "Microsoft Teams", ["Teams"] = "Microsoft Teams",
        ["olk"] = "Outlook", ["OUTLOOK"] = "Outlook", ["WINWORD"] = "Word", ["EXCEL"] = "Excel", ["POWERPNT"] = "PowerPoint",
        ["ONENOTE"] = "OneNote", ["OfficeClickToRun"] = "Microsoft Office Click-to-Run", ["HxTsr"] = "Mail and Calendar",
        ["HxOutlook"] = "Mail and Calendar", ["M365Copilot"] = "Microsoft 365 Copilot", ["Copilot"] = "Copilot",
        ["WindowsTerminal"] = "Windows Terminal", ["OpenConsole"] = "Windows Terminal", ["powershell"] = "Windows PowerShell",
        ["pwsh"] = "PowerShell", ["cmd"] = "Command Prompt", ["mstsc"] = "Remote Desktop Connection", ["msrdc"] = "Remote Desktop",
        ["devenv"] = "Visual Studio", ["Code"] = "Visual Studio Code", ["SnippingTool"] = "Snipping Tool", ["mspaint"] = "Paint",
        ["notepad"] = "Notepad", ["Photos"] = "Photos", ["Microsoft.Photos"] = "Photos", ["Clipchamp"] = "Clipchamp",
    };

    private static readonly string[] roots = MicrosoftRoots();

    // Returns the readable name, or null when the executable is not a recognised Microsoft one.
    public static string? Name(string processName, string processPath)
    {
        if (processName.Length == 0 || !names.TryGetValue(processName, out string? name)) return null;
        return processPath.Length == 0 || IsMicrosoftPath(processPath) ? name : null;
    }

    internal static bool IsMicrosoftPath(string path)
    {
        foreach (string root in roots)
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        // Store apps: C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_...\WindowsTerminal.exe
        return path.Contains(@"\WindowsApps\Microsoft", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\WindowsApps\MicrosoftWindows", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Programs\Microsoft VS Code\", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] MicrosoftRoots()
    {
        static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var bases = new[] { Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => x.Length > 0).Distinct();
        // Windows keeps its separator (C:\WindowsApps is not C:\Windows); "\Microsoft" deliberately also covers "Microsoft Office".
        return new[] { Folder(Environment.SpecialFolder.Windows).TrimEnd('\\') + '\\' }
            .Concat(bases.SelectMany(x => new[] { @"\Microsoft", @"\Windows Defender", @"\Windows Defender Advanced Threat Protection", @"\Common Files\microsoft shared", @"\PowerShell" }.Select(s => x + s)))
            .Append(Path.Combine(Folder(Environment.SpecialFolder.CommonApplicationData), "Microsoft"))
            .Append(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Microsoft"))
            .Where(x => x.Length > 1 && Path.IsPathFullyQualified(x)).ToArray();
    }
}
