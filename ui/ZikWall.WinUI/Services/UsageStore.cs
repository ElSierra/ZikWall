using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed class UsageStore(string directory)
{
    private readonly string path = Path.Combine(directory, "daily-usage.json");
    public List<DailyUsage> Load()
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Daily usage file is too large.");
        return JsonSerializer.Deserialize<List<DailyUsage>>(File.ReadAllText(path)) ?? [];
    }
    public void Save(List<DailyUsage> days)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(days));
        File.Move(path + ".tmp", path, true);
    }
}
