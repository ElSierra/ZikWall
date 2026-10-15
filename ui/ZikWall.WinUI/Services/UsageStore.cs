using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed class UsageStore(string directory)
{
    private readonly string path = Path.Combine(directory, "daily-usage.json");
    public string ArchiveDirectory => Path.Combine(directory, "SavedHistory", "Usage");
    public List<DailyUsage> Load()
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Daily usage file is too large.");
        var days = JsonSerializer.Deserialize<List<DailyUsage>>(File.ReadAllText(path)) ?? [];
        if (days.Count > 15360 || days.Any(x => x is null || string.IsNullOrEmpty(x.AppKey) || x.Path is null))
            throw new InvalidDataException("Saved usage is invalid.");
        Archive(days); // Preserve old dates before the live ledger applies its 30-day window.
        return days;
    }
    public void Save(List<DailyUsage> days)
    {
        Archive(days);
        Directory.CreateDirectory(directory);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, days); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    private void Archive(List<DailyUsage> days)
    {
        foreach (var day in days.GroupBy(x => x.Day))
        {
            Directory.CreateDirectory(ArchiveDirectory);
            string savedPath = Path.Combine(ArchiveDirectory, day.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".json");
            var existing = File.Exists(savedPath) ? JsonSerializer.Deserialize<List<DailyUsage>>(File.ReadAllText(savedPath))
                ?? throw new InvalidDataException("Saved usage archive is empty.") : [];
            var merged = existing.Concat(day).GroupBy(x => x.AppKey).Select(group => group.Last() with
                { Received = group.Max(x => x.Received), Sent = group.Max(x => x.Sent) }).OrderBy(x => x.AppKey, StringComparer.Ordinal).ToList();
            if (existing.SequenceEqual(merged)) continue;
            using (var file = new FileStream(savedPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, merged); file.Flush(true); }
            File.Move(savedPath + ".tmp", savedPath, true);
        }
    }
}
