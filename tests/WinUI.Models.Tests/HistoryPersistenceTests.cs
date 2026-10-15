using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class HistoryPersistenceTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        string directory = Path.Combine(Path.GetTempPath(), "zikwall-retention-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var entry = new ConnectionEntry("TCPv4", "127.0.0.1", 12345, "192.0.2.1", 443, 10, "Established");
            var old = new ConnectionEvent(now.AddDays(-90), "First seen", entry);
            var recent = old with { Time = now };
            var legacy = new ActivityArchive([old, recent], []);
            File.WriteAllText(Path.Combine(directory, "activity.json"), JsonSerializer.Serialize(legacy));
            var store = new ActivityStore(directory);
            var live = store.Load();
            List<ConnectionEvent> SavedEvents() => Directory.EnumerateFiles(store.ArchiveDirectory, "*.jsonl")
                .SelectMany(File.ReadLines).Where(x => !string.IsNullOrWhiteSpace(x))
                .SelectMany(x => JsonSerializer.Deserialize<ActivityArchive>(x)!.Events).ToList();
            Check(live.Events.SequenceEqual([recent]) && SavedEvents().Contains(old),
                "Upgrade must archive old records before trimming the recent view.");
            store.Save(live);
            var replacement = new ActivityStore(directory);
            replacement.Save(replacement.Load());
            Check(SavedEvents().Count == 2, "Reopening and saving must not duplicate migrated records.");
            var burst = Enumerable.Range(0, ActivityStore.MaximumEvents + 1)
                .Select(i => recent with { Time = now.AddTicks(i + 1) }).ToList();
            replacement.Save(new(burst, []));
            Check(SavedEvents().Count == ActivityStore.MaximumEvents + 3 && SavedEvents().Contains(old),
                "The live-view event cap must not delete or omit archived records.");
            var tail = recent with { Time = now.AddSeconds(10), Event = "No longer observed" };
            replacement.Append(new([tail], [])); // Simulate exit before the recent cache is checkpointed.
            Check(new ActivityStore(directory).Load().Events.Contains(tail),
                "Restart must recover flushed records newer than the recent cache.");
            File.Delete(replacement.FilePath);
            Check(new ActivityStore(directory).Load().Events.Contains(tail), "A missing recent cache must rebuild from the archive.");
            Check(SavedEvents().Contains(old), "Rebuilding the recent cache must retain old archive files.");
            string journal = Path.Combine(store.ArchiveDirectory, now.ToString("yyyy-MM-dd") + ".jsonl");
            File.AppendAllText(journal, "{\"Events\":[");
            var recoveredTail = tail with { Time = tail.Time.AddSeconds(1) };
            replacement.Append(new([recoveredTail], []));
            Check(new ActivityStore(directory).Load().Events.Contains(recoveredTail) && File.ReadAllText(journal).Contains("{\"Events\":[\n"),
                "An interrupted append must not prevent subsequent records from loading or truncate the existing journal.");
            var usage = new UsageStore(directory);
            var oldUsage = new DailyUsage(DateOnly.FromDateTime(now.AddDays(-90).Date), entry.AppKey, "fixture.exe", 100, 20);
            var todayUsage = oldUsage with { Day = DateOnly.FromDateTime(now.Date), Received = 200 };
            File.WriteAllText(Path.Combine(directory, "daily-usage.json"), JsonSerializer.Serialize(new[] { oldUsage, todayUsage }));
            usage.Load();
            usage.Save([todayUsage with { Received = 300 }]);
            var savedUsage = Directory.EnumerateFiles(usage.ArchiveDirectory, "*.json")
                .SelectMany(x => JsonSerializer.Deserialize<List<DailyUsage>>(File.ReadAllText(x))!).ToList();
            Check(savedUsage.Contains(oldUsage) && savedUsage.Single(x => x.Day == todayUsage.Day).Received == 300,
                "Usage archives must preserve old dates while updating current cumulative totals.");
            store.Clear();
            Check(!Directory.Exists(store.ArchiveDirectory) && !File.Exists(store.FilePath), "Explicit Clear history must remove cached and archived activity.");
            Check(Directory.Exists(usage.ArchiveDirectory), "Clearing activity must not remove unrelated usage totals.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} indefinite archive, upgrade, recovery and explicit deletion checks.");
    }
}
