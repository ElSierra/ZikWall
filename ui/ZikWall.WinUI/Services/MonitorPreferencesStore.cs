using System.Text.Json;

namespace ZikWall.WinUI.Services;

public sealed record MonitorPreferences(bool HideWindowsSystemApps = false,
    int Group = 0, int Direction = 0, int Scope = 1, int Refresh = 1, int HistoryRange = 0,
    bool RecentAlerts = true, bool DesktopAlerts = false, bool SaveHistory = false,
    string? AdapterId = null, int UsageSort = 0, bool UsageCollection = false, bool DomainAccess = false, bool TcpDetails = false)
{
    public MonitorPreferences Validated() => this with
    {
        Group = ValidIndex(Group, 1, 0), Direction = ValidIndex(Direction, 2, 0),
        Scope = ValidIndex(Scope, 2, 1), Refresh = ValidIndex(Refresh, 2, 1),
        HistoryRange = ValidIndex(HistoryRange, 2, 0), UsageSort = ValidIndex(UsageSort, 2, 0)
    };
    private static int ValidIndex(int value, int max, int fallback) => value >= 0 && value <= max ? value : fallback;
}

public sealed class MonitorPreferencesStore(string directory)
{
    private readonly string path = Path.Combine(directory, "monitor-preferences.json");
    public MonitorPreferences Load()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Monitor preferences are too large.");
        return (JsonSerializer.Deserialize<MonitorPreferences>(File.ReadAllText(path)) ?? new()).Validated();
    }
    public void Save(MonitorPreferences preferences)
    {
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences.Validated());
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}
