using System.Diagnostics;
using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class HistoryPersistenceBenchmarks
{
    public static void Run(string output)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(output))!;
        string directory = Path.Combine(parent, "history-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        void Measure(string name, int iterations, Action operation)
        {
            for (int i = 0; i < 3; i++) operation();
            var samples = new double[iterations];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp(); operation();
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations;
            Array.Sort(samples);
            var result = new { Scenario = name, Iterations = iterations, MedianMs = samples[iterations / 2],
                P95Ms = samples[(int)Math.Ceiling(iterations * .95) - 1], AllocatedBytesPerOperation = allocated };
            results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
        }
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var entry = new ConnectionEntry("TCPv4", "127.0.0.1", 12345, "192.0.2.1", 443, 10, "Established", "Fixture", @"C:\Fixture\app.exe");
            var archive = new ActivityArchive(Enumerable.Range(0, 10000).Select(i => new ConnectionEvent(now.AddTicks(-i), "First seen", entry with { LocalPort = 1000 + i })).ToList(),
                Enumerable.Range(0, 15000).Select(i => new TrafficSample(now.AddSeconds(-i), "fixture", "Fixture adapter", i, i)).ToList());
            var store = new ActivityStore(directory);
            store.Save(archive);
            long JournalBytes() => Directory.EnumerateFiles(store.ArchiveDirectory, "*.jsonl").Sum(x => new FileInfo(x).Length);
            long before = JournalBytes();
            Measure("Append unchanged 10k-event / 15k-sample window", 100, () => store.Append(archive));
            if (JournalBytes() != before) throw new Exception("Unchanged snapshots grew the archive.");
            long sequence = 0;
            Measure("Append and flush 100 new events with a full live window", 30, () =>
            {
                archive.Events.RemoveRange(0, 100);
                archive.Events.AddRange(Enumerable.Range(0, 100).Select(_ => new ConnectionEvent(now.AddTicks(++sequence), "First seen", entry)));
                store.Append(archive);
            });
            Measure("Checkpoint full live window with durable flush", 20, () => store.Save(archive));
            Measure("Restart from full checkpoint and daily journal", 10, () =>
            {
                var loaded = new ActivityStore(directory).Load();
                if (loaded.Events.Count != 10000 || loaded.Traffic.Count != 15000) throw new Exception("Restart lost recent records.");
            });
            File.Delete(store.FilePath);
            Measure("Rebuild missing cache from 13,300 archived events", 10, () =>
            {
                if (new ActivityStore(directory).Load().Events.Count != 10000) throw new Exception("Archive replay did not restore the bounded view.");
            });
            var usage = new UsageStore(directory);
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            var days = Enumerable.Range(0, 15360).Select(i => new DailyUsage(today.AddDays(-(i / 512)), "fixture:" + i % 512, "fixture.exe", 1000, 1000)).ToList();
            usage.Save(days);
            Measure("Save 512 apps across 30 usage days, update today's totals", 10, () =>
            {
                for (int i = 0; i < 512; i++) days[i] = days[i] with { Received = days[i].Received + 1 };
                usage.Save(days);
            });
            File.WriteAllText(output, JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow, FixtureOnly = true,
                JournalBytes = JournalBytes(), Results = results, CorrectnessChecksPassed = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(directory, true); }
    }
}
