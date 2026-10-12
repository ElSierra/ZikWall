using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed record ActivityArchive(List<ConnectionEvent> Events, List<TrafficSample> Traffic);

public sealed class ActivityStore(string directory)
{
    public const int MaximumEvents = 10000;
    public const int MaximumSamples = 15000;
    public string FilePath => Path.Combine(directory, "activity.json");
    public ActivityArchive Load()
    {
        if (!File.Exists(FilePath)) return new([], []);
        if (new FileInfo(FilePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Saved activity exceeds the size limit.");
        using var file = File.OpenRead(FilePath);
        var archive = JsonSerializer.Deserialize<ActivityArchive>(file) ?? throw new InvalidDataException("Saved activity is empty.");
        if (archive.Events is null || archive.Traffic is null || archive.Events.Count > MaximumEvents || archive.Traffic.Count > MaximumSamples
            || archive.Events.Any(x => x is null || x.Connection is null) || archive.Traffic.Any(x => x is null))
            throw new InvalidDataException("Saved activity is invalid.");
        return Trim(archive, DateTimeOffset.UtcNow);
    }
    public void Save(ActivityArchive archive)
    {
        Directory.CreateDirectory(directory);
        string temporary = FilePath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, Trim(archive, DateTimeOffset.UtcNow));
            file.Flush(true);
        }
        File.Move(temporary, FilePath, true);
    }
    public void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
    public static ActivityArchive Trim(ActivityArchive archive, DateTimeOffset now)
    {
        var copy = new ActivityArchive(archive.Events.ToList(), archive.Traffic.ToList());
        TrimInPlace(copy, now);
        return copy;
    }
    // The UI owns these lists. Trim without copying all retained items every refresh.
    // Save/Load continue using the copy-returning API for isolation.
    public static void TrimInPlace(ActivityArchive archive, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-7);
        archive.Events.RemoveAll(x => x.Time < cutoff);
        archive.Traffic.RemoveAll(x => x.Time < cutoff);
        if (archive.Events.Count > MaximumEvents) archive.Events.RemoveRange(0, archive.Events.Count - MaximumEvents);
        if (archive.Traffic.Count > MaximumSamples) archive.Traffic.RemoveRange(0, archive.Traffic.Count - MaximumSamples);
    }
}
