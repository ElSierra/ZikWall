using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

internal static class PassiveDnsBenchmarks
{
    public static void Run(string output)
    {
        var results = new List<object>(); long sink = 0;
        using var process = Process.GetCurrentProcess();
        void Measure(string name, int iterations, int operations, Action action)
        {
            var warmup = Stopwatch.StartNew();
            do { action(); } while (warmup.ElapsedMilliseconds < 1000);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var times = new double[iterations];
            long allocations = GC.GetAllocatedBytesForCurrentThread();
            var cpu = process.TotalProcessorTime;
            for (int i = 0; i < iterations; i++) { long start = Stopwatch.GetTimestamp(); action(); times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
            double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            Array.Sort(times);
            var result = new { Name = name, Iterations = iterations, OperationsPerIteration = operations,
                MedianMs = times[iterations / 2], P95Ms = times[(int)Math.Ceiling(iterations * .95) - 1],
                NanosecondsPerOperation = times[iterations / 2] * 1000000 / operations,
                AllocatedBytesPerOperation = bytes / (double)(iterations * operations), MeanCpuMsPerIteration = cpuMs / iterations };
            results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
        }
        // Known, complete response with compressed owner: bench.example -> 192.0.2.25.
        byte[] dns = Convert.FromHexString("1234818000010001000000000562656E6368076578616D706C650000010001C00C000100010000003C0004C0000219");
        var frame = new byte[14 + 20 + 8 + dns.Length]; frame[12] = 8; frame[14] = 0x45; frame[23] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)(frame.Length - 14));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34), 53); BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38), (ushort)(dns.Length + 8)); dns.CopyTo(frame, 42);
        if (DnsResponseParser.Parse(DnsPacketParser.Payload(frame)).Single().Name != "bench.example") throw new IOException("DNS fixture failed.");
        var unrelated = frame.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(unrelated.AsSpan(34), 443);
        Measure("Reject non-DNS UDP packets", 25, 1000000, () => { for (int i = 0; i < 1000000; i++) sink += DnsPacketParser.Payload(unrelated).Length; });
        Measure("Parse complete Ethernet/IPv4 DNS responses", 30, 10000, () => { for (int i = 0; i < 10000; i++) sink += DnsResponseParser.Parse(DnsPacketParser.Payload(frame)).Count; });
        var warm = new ObservedDnsCache(() => 0);
        Measure("Parse and cache repeated DNS responses", 30, 10000, () => { for (int i = 0; i < 10000; i++) warm.Observe(DnsResponseParser.Parse(DnsPacketParser.Payload(frame))); });
        long memoryBefore = GC.GetTotalMemory(true);
        var full = new ObservedDnsCache(() => 0);
        for (int i = 0; i < ObservedDnsCache.Capacity; i++) full.Observe([new($"10.0.{i / 256}.{i % 256}", $"n{i}.example", 3600, true)]);
        long retained = GC.GetTotalMemory(true) - memoryBefore;
        var wanted = Enumerable.Range(0, 2048).Select(i => $"10.0.{i / 256}.{i % 256}").ToHashSet();
        Measure("Read 2048 IPs from full 8192-entry cache", 50, 1, () => sink += full.Read(wanted).Count);
        int sequence = 0;
        Measure("Insert new names into full cache (eviction stress)", 20, 256, () => { for (int i = 0; i < 256; i++) full.Observe([new("192.0.2.25", $"churn{sequence++}.example", 3600, true)]); });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow, OS = Environment.OSVersion.ToString(), LogicalProcessors = Environment.ProcessorCount,
            Scenario = "Warmed synthetic parser/cache workloads; no capture session, packets sent, native callback, IPC or UI rendering. Full-cache retained memory is an approximate managed-heap delta, not total process memory.",
            FullCacheEntries = ObservedDnsCache.Capacity, ApproximateFullCacheRetainedBytes = retained, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(full); GC.KeepAlive(warm); GC.KeepAlive(sink);
    }
}
