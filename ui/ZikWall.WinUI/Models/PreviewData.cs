namespace ZikWall.WinUI.Models;

public static class PreviewData
{
    public static List<RuleEntry> Create() =>
    [
        new("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Applications", "Allow", "Any destination", Notes: "Example browser permission."),
        new("Visual Studio Code", @"C:\Users\Example\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Applications", "Allow", "Any destination"),
        new("Spotify", @"C:\Users\Example\AppData\Roaming\Spotify\Spotify.exe", "Applications", "Allow", "Any destination"),
        new("OneDrive", @"C:\Program Files\Microsoft OneDrive\OneDrive.exe", "Custom rules", "Block", "Any destination", Notes: "Example outbound block rule."),
        new("Steam", @"C:\Program Files (x86)\Steam\steam.exe", "Custom rules", "Block", "Any destination"),
        new("Signal", @"C:\Users\Example\AppData\Local\Programs\signal-desktop\Signal.exe", "Applications", "Allow", "Any destination", Expires: DateTimeOffset.UtcNow.AddMinutes(30)),
        new("DNS", "", "System rules", "Allow", "Port 53", "UDP", Notes: "Example system rule for DNS queries."),
        new("Local network", "", "System rules", "Allow", "192.168.0.0/16", Direction: "Both"),
        new("Telemetry example", "", "Blocklists", "Block", "192.0.2.0/24", Notes: "Documentation-only example address range."),
        new("Windows Update", "wuauserv", "System rules", "Allow", "Any destination", Enabled: false)
    ];
}
