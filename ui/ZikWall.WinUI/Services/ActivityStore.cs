using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed record ActivityArchive(List<ConnectionEvent> Events, List<TrafficSample> Traffic);

public sealed class ActivityStore(string directory)
{
    public const int MaximumEvents = 10000;
    public const int MaximumSamples = 15000;
    public string FilePath => Path.Combine(directory, "activity.json");
    public string ArchiveDirectory => Path.Combine(directory, "SavedHistory", "Activity");
    private readonly HashSet<ConnectionEvent> archivedEvents = [];
    private readonly HashSet<TrafficSample> archivedTraffic = [];
    public ActivityArchive Load()
    {
        if (!File.Exists(FilePath)) return RestoreTail(new([], []));
        if (new FileInfo(FilePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Saved activity exceeds the size limit.");
        using var file = File.OpenRead(FilePath);
        var archive = JsonSerializer.Deserialize<ActivityArchive>(file) ?? throw new InvalidDataException("Saved activity is empty.");
        if (archive.Events is null || archive.Traffic is null || archive.Events.Count > MaximumEvents || archive.Traffic.Count > MaximumSamples
            || archive.Events.Any(x => x is null || x.Connection is null) || archive.Traffic.Any(x => x is null))
            throw new InvalidDataException("Saved activity is invalid.");
        // Migrate the complete old snapshot before applying the live-view bounds.
        // On later launches this cache contains records already appended to disk.
        if (!File.Exists(Path.Combine(ArchiveDirectory, "format-v1.txt"))) Append(archive);
        else Remember(archive);
        return RestoreTail(Trim(archive, DateTimeOffset.UtcNow));
    }
    public void Save(ActivityArchive archive)
    {
        Append(archive);
        Directory.CreateDirectory(directory);
        string temporary = FilePath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, Trim(archive, DateTimeOffset.UtcNow));
            file.Flush(true);
        }
        File.Move(temporary, FilePath, true);
        // Deduplication only needs the bounded live snapshot; the archive is never trimmed.
        archivedEvents.IntersectWith(archive.Events);
        archivedTraffic.IntersectWith(archive.Traffic);
    }
    public void Append(ActivityArchive archive)
    {
        var events = archive.Events.Where(x => !archivedEvents.Contains(x)).ToList();
        var samples = archive.Traffic.Where(x => !archivedTraffic.Contains(x)).ToList();
        Directory.CreateDirectory(ArchiveDirectory);
        foreach (var day in events.Select(x => x.Time.UtcDateTime.Date).Concat(samples.Select(x => x.Time.UtcDateTime.Date)).Distinct())
        {
            var batch = new ActivityArchive(events.Where(x => x.Time.UtcDateTime.Date == day).ToList(),
                samples.Where(x => x.Time.UtcDateTime.Date == day).ToList());
            string path = Path.Combine(ArchiveDirectory, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            // Separate a possible interrupted last record from the next valid batch.
            // Never truncate existing bytes during recovery.
            if (file.Length > 0) file.WriteByte((byte)'\n');
            JsonSerializer.Serialize(file, batch);
            file.WriteByte((byte)'\n');
            file.Flush(true);
            Remember(batch);
        }
        string marker = Path.Combine(ArchiveDirectory, "format-v1.txt");
        if (!File.Exists(marker)) File.WriteAllText(marker, "Daily JSON Lines batches of connection events and adapter samples. Kept until Clear history. activity.json is only the recent-view cache.");
    }
    private void Remember(ActivityArchive archive)
    {
        archivedEvents.UnionWith(archive.Events);
        archivedTraffic.UnionWith(archive.Traffic);
    }
    private ActivityArchive RestoreTail(ActivityArchive archive)
    {
        if (!Directory.Exists(ArchiveDirectory)) return archive;
        var now = DateTimeOffset.UtcNow;
        var eventCutoff = archive.Events.Select(x => x.Time).DefaultIfEmpty(now.AddDays(-7)).Max();
        var trafficCutoff = archive.Traffic.Select(x => x.Time).DefaultIfEmpty(now.AddDays(-7)).Max();
        var firstDay = (eventCutoff < trafficCutoff ? eventCutoff : trafficCutoff).UtcDateTime.Date;
        foreach (string path in Directory.EnumerateFiles(ArchiveDirectory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) || day < firstDay) continue;
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ActivityArchive? batch;
                try { batch = JsonSerializer.Deserialize<ActivityArchive>(line); }
                catch (JsonException) { continue; } // A power loss can interrupt the last append; its bytes remain on disk.
                if (batch?.Events is null || batch.Traffic is null) continue;
                archive.Events.AddRange(batch.Events.Where(x => x is not null && x.Connection is not null && x.Time > eventCutoff && archivedEvents.Add(x)));
                archive.Traffic.AddRange(batch.Traffic.Where(x => x is not null && x.Time > trafficCutoff && archivedTraffic.Add(x)));
                // Bound replay memory without re-hashing the entire live window for
                // every small batch in a busy day's journal.
                if (archive.Events.Count > MaximumEvents * 2 || archive.Traffic.Count > MaximumSamples * 2)
                {
                    TrimInPlace(archive, now);
                    archivedEvents.IntersectWith(archive.Events);
                    archivedTraffic.IntersectWith(archive.Traffic);
                }
            }
        }
        TrimInPlace(archive, now);
        archivedEvents.IntersectWith(archive.Events);
        archivedTraffic.IntersectWith(archive.Traffic);
        return archive;
    }
    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        if (Directory.Exists(ArchiveDirectory)) Directory.Delete(ArchiveDirectory, true);
        archivedEvents.Clear(); archivedTraffic.Clear();
    }
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
