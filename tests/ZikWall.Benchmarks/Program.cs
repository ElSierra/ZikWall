using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

// No test framework/package, real firewall changes, public requests or user-data writes.
string output = args.FirstOrDefault() ?? "artifacts/benchmarks/core.json";
var results = new List<object>();
using var benchmarkProcess = Process.GetCurrentProcess();
object? keepAlive = null;
void Measure(string name, int iterations, Func<object?> operation)
{
    for (int i = 0; i < 5; i++) keepAlive = operation();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long allocations = GC.GetAllocatedBytesForCurrentThread();
    var timings = new double[iterations];
    var cpuStart = benchmarkProcess.TotalProcessorTime;
    for (int i = 0; i < iterations; i++)
    {
        long start = Stopwatch.GetTimestamp();
        keepAlive = operation();
        timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocations;
    double cpu = (benchmarkProcess.TotalProcessorTime - cpuStart).TotalMilliseconds;
    Array.Sort(timings);
    var result = new { Name = name, Iterations = iterations, MedianMs = timings[iterations / 2], P95Ms = timings[(int)Math.Ceiling(iterations * .95) - 1], MeanCpuMs = cpu / iterations, AllocatedBytesPerOperation = allocated / iterations };
    results.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
}
var now = DateTimeOffset.UtcNow;
var rows = Enumerable.Range(0, 10000).Select(i => new ConnectionEntry("TCPv4", "127.0.0.1", i + 1000, "192.0.2.1", 443, i % 100, "Established", "fixture", @"C:\Tests\fixture.exe")).ToArray();
var groupedRows = rows.Select((x, i) => x with { ProcessPath = $@"C:\Tests\app{i % 100}.exe", ProcessName = $"App {i % 100}" }).ToArray();
var tree = new ActivityTree(); tree.Update(groupedRows);
Measure("Group 10,000 connections into 100 collapsed apps", 100, () => { tree.Update(groupedRows); return tree.Rows; });
var ledger = new UsageLedger();
var today = DateOnly.FromDateTime(now.LocalDateTime);
var usageApps = Enumerable.Range(0, 512).Select(i => new AppTraffic($@"C:\Tests\usage{i}.exe", "", 0, 0)).ToList();
ledger.Load(Enumerable.Range(0, 15360).Select(i => new DailyUsage(today.AddDays(-(i / 512)), usageApps[i % 512].AppKey, usageApps[i % 512].Path, 1000, 1000)), today);
var usageSession = Guid.NewGuid(); ulong sequence = 0;
Measure("Update 512 apps with 30 daily usage buckets each", 100, () =>
{
    sequence++;
    ledger.Observe(new(usageSession, now.AddSeconds(sequence * 5), usageApps.Select(x => x with { Received = sequence * 100, Sent = sequence * 20 }).ToList(), sequence, 0, 0, 0, 1024));
    return ledger.Days;
});
var tracker = new ConnectionTracker();
tracker.Observe(rows, now);
var archive = new ActivityArchive(rows.Select(x => new ConnectionEvent(now, "First seen", x)).ToList(), Enumerable.Range(0, 15000).Select(i => new TrafficSample(now.AddSeconds(-i), "fixture", "Fixture adapter", i, i)).ToList());
Measure("Track 10,000 unchanged endpoints", 100, () => tracker.Observe(rows, now));
var observableRows = new ConnectionCollection(); observableRows.Update(rows);
Measure("Update 10,000 unchanged UI rows", 100, () => { observableRows.Update(rows); return observableRows; });
Measure("Search 10,000 endpoints (empty)", 100, () => rows.Count(x => x.Matches("")));
Measure("Search 10,000 endpoints (address)", 100, () => rows.Count(x => x.Matches("192.0.2")));
Measure("Trim full 10k-event / 15k-sample archive", 100, () => ActivityStore.Trim(archive, now));
Measure("Trim full archive in place (monitor path)", 100, () => { ActivityStore.TrimInPlace(archive, now); return archive; });
var buffer = new byte[4 + 24 * rows.Length];
BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
Measure("Parse 10,000 IPv4 TCP rows", 100, () => ConnectionMonitor.Parse(buffer, true, false));
var rules = Enumerable.Range(1, 1500).Select(i => new FirewallRule(Guid.NewGuid(), "Fixture", @"C:\Tests\fixture.exe", "192.0.2.1", 6, i, 1, true, true, null)).ToArray();
Measure("Analyze 1,500 rules (bounded worst case)", 10, () => PolicyAnalysis.Analyze(rules, now, _ => true));
string domainText = string.Join('\n', Enumerable.Range(0, 100000).Select(i => $"block{i}.example"));
Measure("Parse 100,000 blocklist domains", 10, () => BlocklistParser.Parse(domainText));
string geoText = "network,latitude,longitude,label\n" + string.Join('\n', Enumerable.Range(0, 50000).Select(i => $"10.{i / 256}.{i % 256}.0/24,0,0,Fixture"));
using var geoInput = new MemoryStream(Encoding.UTF8.GetBytes(geoText));
var geo = GeoDatabase.Read(geoInput);
Measure("Find 1,000 IPs in 50,000 geolocation networks", 100, () =>
{
    GeoLocation? found = null;
    for (int i = 0; i < 1000; i++) found = geo.Find($"10.{i / 256}.{i % 256}.1");
    return found;
});
int endpoints = 0;
if (OperatingSystem.IsWindows())
{
    endpoints = ConnectionMonitor.Capture().Count;
    Measure("Capture live Windows endpoints", 30, () => ConnectionMonitor.Capture());
    Measure("Capture live adapter counters", 30, () => ConnectionMonitor.CaptureTraffic());
    using var enrichment = new MonitorEnrichment("ui/ZikWall.WinUI/Assets/Countries");
    var captured = ConnectionMonitor.Capture();
    Measure("Enrich live endpoints with direction and country", 30, () => enrichment.Enrich(captured));
}
using (var countries = new CountryDatabase("ui/ZikWall.WinUI/Assets/Countries"))
    Measure("10,000 offline country lookups (cache churn)", 30, () =>
    {
        for (int i = 0; i < 10000; i++) keepAlive = countries.Find($"8.{i / 256}.{i % 256}.1");
        return keepAlive;
    });
// Real local UDP round trips. Every query is blocked locally; no upstream or OS DNS changes.
using (var proxy = new DnsProxy(new Uri("https://resolver.invalid/dns-query"), IPAddress.Loopback, 0))
using (var socket = new UdpClient())
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
{
    proxy.SetLists(["ads.example"], []);
    byte[] query = [0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, (byte)'a', (byte)'d', (byte)'s', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 0, 0, 1, 0, 1];
    var destination = new IPEndPoint(IPAddress.Loopback, proxy.LocalPort);
    async Task QueryAsync()
    {
        await socket.SendAsync(query, destination, timeout.Token);
        var response = (await socket.ReceiveAsync(timeout.Token)).Buffer;
        if ((response[3] & 15) != 3) throw new Exception("Expected locally blocked DNS answer.");
    }
    for (int i = 0; i < 10; i++) await QueryAsync();
    var timings = new double[1000];
    var elapsed = Stopwatch.StartNew();
    var cpuStart = benchmarkProcess.TotalProcessorTime;
    for (int i = 0; i < timings.Length; i++)
    {
        long start = Stopwatch.GetTimestamp(); await QueryAsync();
        timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    elapsed.Stop(); Array.Sort(timings);
    if (proxy.Counts.Errors != 0 || proxy.Counts.Blocked != 1010) throw new Exception("DNS benchmark reported errors or missed queries.");
    var result = new { Name = "1,000 sequential local UDP DNS blocks", Queries = timings.Length, MedianMs = timings[500], P95Ms = timings[949], QueriesPerSecond = timings.Length / elapsed.Elapsed.TotalSeconds, TotalCpuMs = (benchmarkProcess.TotalProcessorTime - cpuStart).TotalMilliseconds };
    results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new { TimestampUtc = now, OS = Environment.OSVersion.ToString(), LogicalProcessors = Environment.ProcessorCount, LiveEndpointCount = endpoints, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
GC.KeepAlive(keepAlive);
