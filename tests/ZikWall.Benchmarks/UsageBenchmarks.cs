using System.Diagnostics;
using System.Text.Json;
using ZikWall.WinUI.Models;

static class UsageBenchmarks
{
    public static void Run(string output)
    {
        var results = new List<object>();
        void Measure(string name, Action operation)
        {
            for (int i = 0; i < 10; i++) operation();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var timings = new double[100];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < timings.Length; i++)
            {
                long start = Stopwatch.GetTimestamp(); operation();
                timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / timings.Length;
            Array.Sort(timings);
            var result = new { Name = name, MedianMs = timings[50], P95Ms = timings[94], AllocatedBytesPerOperation = allocated };
            results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
        }
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        var apps = Enumerable.Range(0, 512).Select(i => new AppTraffic($@"C:\Tests\app{i}.exe", "", 0, 0)).ToArray();
        var keys = apps.Select(x => x.AppKey).ToArray();
        var ledger = new UsageLedger();
        ledger.Load(Enumerable.Range(0, 15360).Select(i => new DailyUsage(today.AddDays(-(i / 512)), keys[i % 512], apps[i % 512].Path, (ulong)(i + 1) * 1000, 1000)), today);
        Measure("Read usage labels for 512 apps from 30-day history", () =>
        {
            foreach (string key in keys) ledger.Labels(key, true, today);
        });
        var rows = Enumerable.Range(0, 10000).Select(i => new ConnectionEntry("TCPv4", "192.0.2.2", i + 1000,
            "192.0.2.1", 443, i % 512, "Established", $"App {i % 512:D3}", apps[i % 512].Path)).ToArray();
        var tree = new ActivityTree();
        Measure("Rebuild 10,000 connections with 512 app usage labels", () => tree.Update(rows, showUsage: true, sort: UsageSort.Descending, usageDisplay: key => ledger.Display(key, today)));
        tree.Update(rows, showUsage: true, sort: UsageSort.Descending, usageDisplay: key => ledger.Display(key, today));
        Measure("Refresh and sort 512 usage cells without regrouping 10,000 connections", () => tree.ApplyUsage(true, UsageSort.Descending, key => ledger.Display(key, today)));
        var session = Guid.NewGuid(); ulong tick = 0;
        Measure("Observe 512 apps with 30-day history", () =>
        {
            tick++;
            ledger.Observe(new(session, now.AddSeconds(tick * 5), apps.Select(x => x with { Received = tick * 100, Sent = tick * 20 }).ToList(), tick, 0, 0, 0, 1024));
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow, OS = Environment.OSVersion.ToString(), LogicalProcessors = Environment.ProcessorCount, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
