using System.Diagnostics;
using System.Text.Json;
using ZikWall.WinUI.Models;

internal static class ActivityAllocationBenchmarks
{
    internal static void Run(string output)
    {
        var rows = Enumerable.Range(0, 10000).Select(i => new ConnectionEntry("TCPv4", "192.0.2.2", i + 1000,
            "192.0.2.1", 443, i % 100, "Established", $"App {i % 100}", $@"C:\Tests\app{i % 100}.exe")).ToArray();
        var fresh = new HashSet<string>();
        var tree = new ActivityTree();
        for (int i = 0; i < 5; i++) tree.Update(rows, fresh: fresh);
        const int iterations = 100;
        var times = new double[iterations];
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            tree.Update(rows, fresh: fresh);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - startBytes) / iterations;
        Array.Sort(times);
        var result = new { Scenario = "10,000 connections, 100 collapsed apps, empty new-connection set (production monitor path)",
            Iterations = iterations, AllocatedBytesPerRefresh = bytes, MedianMs = times[iterations / 2], P95Ms = times[94],
            AllocationBudget = 8_000_000, Passed = bytes <= 8_000_000 && tree.Rows.Count == 100 && tree.Rows.All(x => !x.IsNew) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json);
        Console.WriteLine(json);
        if (!result.Passed) throw new InvalidOperationException("Live grouping exceeded its allocation budget or changed the fixture result.");
    }
}
