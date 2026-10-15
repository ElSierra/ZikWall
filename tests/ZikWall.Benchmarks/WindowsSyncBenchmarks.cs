using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

internal static class WindowsSyncBenchmarks
{
    private sealed class ReadOnlyStore(IWindowsFirewallStore inner) : IWindowsFirewallStore
    {
        public List<WindowsFirewallEntry> Read() => inner.Read();
        public List<WindowsFirewallEntry> ReadNames(IReadOnlySet<string> names) => inner.ReadNames(names);
        public void Remove(string name) => throw new InvalidOperationException("Benchmark prohibits native writes.");
        public void Add(WindowsFirewallEntry entry) => throw new InvalidOperationException("Benchmark prohibits native writes.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, PageFile, PeakPageFile, PrivateBytes;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
    private sealed record Measurement(string Name, int Iterations, double MedianMs, double P95Ms, double MeanCpuMs, long AllocatedBytesPerOperation);
    internal static void Run(string output, bool native, bool polling)
    {
        using var process = Process.GetCurrentProcess();
        var results = new List<Measurement>();
        object? keepAlive = null;
        Measurement Measure(string name, int iterations, Func<object> operation)
        {
            for (int i = 0; i < 4; i++) keepAlive = operation();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var timings = new double[iterations];
            var cpuStart = process.TotalProcessorTime;
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp(); keepAlive = operation();
                timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            double cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            Array.Sort(timings);
            var result = new Measurement(name, iterations, timings[iterations / 2], timings[(int)Math.Ceiling(iterations * .95) - 1], cpu / iterations, allocated / iterations);
            results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result)); return result;
        }
        WindowsFirewallPlan Plan(NetworkProfile profile, WindowsFirewallSyncState? state, List<WindowsFirewallEntry> entries)
        {
            var plan = WindowsFirewallSync.Plan(profile, state, entries, []);
            if (!plan.Ready) throw new InvalidOperationException(string.Join("; ", plan.Problems));
            return plan;
        }
        var gates = new List<bool>();
        if (!polling)
        {
            foreach (int count in new[] { 10, 100, 1000 })
            {
                foreach (bool mixed in new[] { false, true })
                {
                    var id = new Guid("dd938020-2787-4878-a674-f8ee92623ab1");
                    var rules = Enumerable.Range(0, count).Select(i => new FirewallRule(new Guid(i + 1, 0, 0, new byte[8]), "Fixture " + i,
                        @"C:\Tests\sync-fixture.exe", $"10.{i / 256}.{i % 256}.0/24", 6, 0, 1, !mixed || i % 2 == 0, true, null)).ToList();
                    var profile = new NetworkProfile(id, "Benchmark", rules);
                    var initial = Plan(profile, null, []);
                    var entries = initial.Links.SelectMany(x => x.Windows).ToList();
                    var state = new WindowsFirewallSyncState(id, initial.Links);
                    string kind = mixed ? "mixed disjoint" : "block";
                    int iterations = count == 1000 ? 15 : 40;
                    var unchanged = Measure($"Plan unchanged {count} {kind} rules", iterations, () =>
                    {
                        var plan = Plan(profile, state, entries);
                        if (plan.Summary.Count != 0 || plan.Rules.Count != count) throw new InvalidOperationException("Unchanged plan produced changes.");
                        return plan;
                    });
                    if (count == 1000) gates.Add(unchanged.MedianMs <= 50 && unchanged.AllocatedBytesPerOperation <= 8_000_000);
                    if (count == 1000)
                    {
                        var changed = profile with { Rules = rules.Select(x => x with { RemotePort = 443 }).ToList() };
                        Measure($"Plan 1000 local edits ({kind})", 10, () =>
                        {
                            var plan = Plan(changed, state, entries);
                            if (plan.Changes.Count != count) throw new InvalidOperationException("Bulk changes were lost.");
                            return plan;
                        });
                    }
                }
            }
        }
        object? pollingResults = null;
        int nativeCount = 0, linkedCount = 0;
        if (native || polling)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var store = new WindowsFirewallStore();
            var all = store.Read(); nativeCount = all.Count;
            var first = all.First(x => x.Rule is not null);
            var selected = all.Where(x => x.Rule is not null && x.Rule.Block == first.Rule!.Block && all.Count(y => y.Name == x.Name) == 1).Take(10).ToList();
            var id = new Guid("344b76a8-fbc3-4c8f-ae8b-30f81b235e88");
            var profile = new NetworkProfile(id, "Read-only benchmark", selected.Select(x => x.Rule!).ToList());
            var state = new WindowsFirewallSyncState(id, selected.Select(x => new WindowsFirewallLink(x.Rule!.Id, x.Rule, [x])).ToList());
            linkedCount = selected.Count;
            object Poll()
            {
                // Same read/plan operation as the production automatic-sync path.
                using var pollingStore = new WindowsFirewallStore();
                return WindowsFirewallPolling.Plan(pollingStore, profile, state);
            }
            if (polling)
            {
                for (int i = 0; i < 4; i++) keepAlive = Poll();
                object Sample(bool enabled)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var startCpu = process.TotalProcessorTime; long allocated = GC.GetTotalAllocatedBytes(true);
                    var clock = Stopwatch.StartNew(); double next = 0; int polls = 0;
                    var privateBytes = new List<long>(); var workingSet = new List<long>(); double sampleAt = 0;
                    while (clock.Elapsed.TotalSeconds < 65)
                    {
                        if (enabled && clock.Elapsed.TotalSeconds >= next) { keepAlive = Poll(); polls++; next += 30; }
                        if (clock.Elapsed.TotalSeconds >= sampleAt)
                        {
                            if (!GetProcessMemoryInfo(process.Handle, out var memory, (uint)Marshal.SizeOf<MemoryCounters>())) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                            privateBytes.Add((long)memory.PrivateBytes); workingSet.Add((long)memory.WorkingSet); sampleAt++;
                        }
                        Thread.Sleep(100);
                    }
                    double elapsed = clock.Elapsed.TotalSeconds;
                    double cpu = (process.TotalProcessorTime - startCpu).TotalMilliseconds;
                    return new { Enabled = enabled, ElapsedSeconds = elapsed, Polls = polls, CpuMs = cpu, PercentOneCore = cpu / (elapsed * 10),
                        AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, MeanPrivateMiB = privateBytes.Average() / 1048576,
                        PeakPrivateMiB = privateBytes.Max() / 1048576.0, MeanWorkingSetMiB = workingSet.Average() / 1048576 };
                }
                Console.WriteLine("Sampling 65 seconds with sync off, then 65 seconds with three read-only polls.");
                var off = Sample(false); Console.WriteLine(JsonSerializer.Serialize(off));
                var on = Sample(true); Console.WriteLine(JsonSerializer.Serialize(on));
                pollingResults = new { Off = off, On = on };
            }
            else
            {
                Measure("Native full inventory read", 15, () => store.Read());
                var names = selected.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Measure("Native targeted read (10 names)", 15, () => store.ReadNames(names));
                Measure("Automatic read + plan (10 linked native rules)", 15, Poll);
                var markerProbe = WindowsFirewallEntry.Create("Absent benchmark identity", WindowsFirewallSync.Marker + "read-only-benchmark", "", selected[0].Rule!);
                Measure("Native linked scan (10 names plus absent marker)", 15, () => store.ReadForSync([.. selected, markerProbe]));
                var replay = new WindowsFirewallBatch(selected.Select(x => new WindowsFirewallChange(x, x)).ToList(), true);
                var readOnly = new ReadOnlyStore(store);
                Measure("Native converged retry (10 rules, writes forbidden)", 8, () => { WindowsFirewallWriter.Apply(readOnly, replay); return true; });
            }
        }
        var report = new { TimestampUtc = DateTimeOffset.UtcNow, OS = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(),
            NativeRules = nativeCount, NativeLinkedRules = linkedCount, Results = results, Polling = pollingResults,
            Budget = "Unchanged 1000-rule planner: median <= 50 ms and managed allocations <= 8 MB per operation", Passed = gates.All(x => x),
            Scope = "Read-only native access. CPU/memory belong to this process, not the Windows firewall service. No firewall writes or traffic throughput measurement." };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!report.Passed) Environment.ExitCode = 1;
        GC.KeepAlive(keepAlive);
    }
}
