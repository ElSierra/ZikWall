using System.Diagnostics;
using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class LogAnalysisBenchmarks
{
    public static void Run(string output)
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var events = Enumerable.Range(0, 10000).Select(i => new ConnectionEvent(now.AddDays(-2).AddSeconds(i),
            "First seen", new("TCPv4", "127.0.0.1", i + 1000, "192.0.2.1", 443, i % 100,
                "Established", $"App {i % 100}", $@"C:\Tests\app{i % 100}.exe"))).ToArray();
        using var enrichment = new MonitorEnrichment("ui/ZikWall.WinUI/Assets/Countries");
        var results = new List<object>();
        bool passed = true;
        foreach (var scenario in new[] { ("Important", LogFilter.Important, LogRange.All, 100),
            ("Today with older history", LogFilter.All, LogRange.Today, 0),
            ("All with display cap", LogFilter.All, LogRange.All, LogAnalysis.DisplayLimit) })
        {
            int calls = 0;
            ConnectionEntry Enrich(ConnectionEntry row) { calls++; return enrichment.WithDomainHint(row); }
            LogAnalysisResult Build() => LogAnalysis.Build(events, now, DateTimeOffset.MinValue,
                scenario.Item2, scenario.Item3, enrich: Enrich, timeZone: TimeZoneInfo.Utc);
            for (int i = 0; i < 5; i++) Build();
            const int iterations = 100;
            calls = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var timings = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                var result = Build();
                timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (result.Groups.Sum(x => x.Count) != scenario.Item4) throw new Exception("Incorrect displayed count.");
            }
            allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations;
            Array.Sort(timings);
            bool budget = calls / iterations == scenario.Item4;
            passed &= budget;
            var report = new { Scenario = scenario.Item1, MedianMs = timings[50], P95Ms = timings[94],
                AllocatedBytesPerOperation = allocated, EnrichmentCalls = calls / iterations,
                ExpectedEnrichmentCalls = scenario.Item4, BudgetPassed = budget };
            results.Add(report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new Exception("Log analysis enriched events that cannot be displayed or searched.");
    }
}
