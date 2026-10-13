using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZikWall.WinUI.Services;

// Explicit diagnostic: no window/monitor, production authenticated capture helper,
// normal host traffic and 20 paced public DNS questions. No rules/settings changed.
internal static class DomainCaptureBenchmark
{
    private sealed record Sample(string Process, int Pid, double Seconds, double CpuMs, double PrivateMiB, double WorkingSetMiB, int Handles);
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var phases = new List<object>(); var tracked = new List<(string Name, Process Process)>();
        string? failure = null; bool cleanup = false; double startupMs = 0;
        using var client = new FirewallClient();
        using var app = Process.GetCurrentProcess(); tracked.Add(("Diagnostic host (no window)", app));
        var before = await PassiveDnsSelfTest.SessionsAsync();
        var queryTimes = new List<double>(); var ipcTimes = new List<double>();
        int sent = 0, answered = 0, timedOut = 0, observedPolls = 0; string lastStatus = "";
        var wanted = new List<string> { "1.0.0.1" };
        string marker = "zikw-bench-" + Guid.NewGuid().ToString("N")[..10] + ".1-0-0-1.sslip.io";
        async Task ReadAsync()
        {
            var watch = Stopwatch.StartNew();
            var response = await client.SendAsync(new("domains", RemoteAddresses: wanted));
            ipcTimes.Add(watch.Elapsed.TotalMilliseconds); lastStatus = response.Message;
            if (!response.Success || response.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || response.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase)) throw new IOException(response.Message);
            if (response.Domains?.Any(x => x.ObservedResponse && x.Name == marker) == true) observedPolls++;
        }
        async Task QueryAsync()
        {
            byte[] query = PassiveDnsSelfTest.Query(marker);
            using var udp = new UdpClient(AddressFamily.InterNetwork); udp.Connect(IPAddress.Parse("1.1.1.1"), 53);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew(); await udp.SendAsync(query); sent++;
            UdpReceiveResult reply;
            try { reply = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { timedOut++; return; }
            queryTimes.Add(watch.Elapsed.TotalMilliseconds);
            if (!reply.Buffer.AsSpan(0, 2).SequenceEqual(query.AsSpan(0, 2)) || !DnsResponseParser.Parse(reply.Buffer).Any(x => x.Name == marker && x.Address == "1.0.0.1")) throw new IOException("Benchmark DNS reply failed validation.");
            answered++;
        }
        async Task MeasureAsync(string label, int seconds, bool capture, bool queries)
        {
            var samples = new List<Sample>(); var initial = new Dictionary<int, double>();
            foreach (var (_, process) in tracked) { process.Refresh(); initial[process.Id] = process.TotalProcessorTime.TotalMilliseconds; }
            var watch = Stopwatch.StartNew();
            for (int tick = 0; tick < seconds; tick++)
            {
                if (capture && tick % 2 == 0) { if (queries) await QueryAsync(); await ReadAsync(); }
                await Task.Delay(1000); // No catch-up bursts after a slow or lost DNS reply.
                foreach (var (name, process) in tracked)
                {
                    process.Refresh(); if (process.HasExited) throw new IOException(name + " exited during measurement.");
                    samples.Add(new(name, process.Id, watch.Elapsed.TotalSeconds, process.TotalProcessorTime.TotalMilliseconds - initial[process.Id],
                        process.PrivateMemorySize64 / 1048576d, process.WorkingSet64 / 1048576d, process.HandleCount));
                }
            }
            var summaries = samples.GroupBy(x => x.Pid).Select(group => new {
                Process = group.First().Process, CpuOneCorePercent = group.Last().CpuMs / (watch.Elapsed.TotalSeconds * 10),
                PeakPrivateMiB = group.Max(x => x.PrivateMiB), PeakWorkingSetMiB = group.Max(x => x.WorkingSetMiB),
                PrivateGrowthMiB = group.Last().PrivateMiB - group.First().PrivateMiB, PeakHandles = group.Max(x => x.Handles) }).ToArray();
            phases.Add(new { Scenario = label, Seconds = watch.Elapsed.TotalSeconds,
                CombinedCpuOneCorePercent = summaries.Sum(x => x.CpuOneCorePercent),
                SumOfPeakPrivateMiB = summaries.Sum(x => x.PeakPrivateMiB), Processes = summaries, Samples = samples });
        }
        try
        {
            await Task.Delay(5000);
            await MeasureAsync("Capture off; diagnostic host without window", 30, false, false);
            var startup = Stopwatch.StartNew(); await client.StartAsync(domainsOnly: true);
            for (int i = 0; i < 50; i++)
            {
                var response = await client.SendAsync(new("status")); lastStatus = response.Message;
                if (!response.Message.StartsWith("Starting", StringComparison.Ordinal)) break;
                await Task.Delay(500);
            }
            startupMs = startup.Elapsed.TotalMilliseconds;
            if (lastStatus.StartsWith("Starting", StringComparison.Ordinal) || lastStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase)) throw new IOException(lastStatus);
            var broker = Process.GetProcessById(client.BrokerProcessId); tracked.Add(("DNS broker", broker));
            using (var query = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId={broker.Id}"))
            using (var children = query.Get())
                foreach (ManagementObject child in children)
                {
                    using (child) { var controller = Process.GetProcessById(Convert.ToInt32(child["ProcessId"]));
                        if (!controller.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase)) { controller.Dispose(); continue; }
                        tracked.Add(("Capture controller", controller)); }
                }
            if (tracked.Count != 3) throw new IOException("Could not account for all three capture processes.");
            await ReadAsync(); await Task.Delay(5000); // Startup/WMI warmup excluded from steady samples.
            ipcTimes.Clear();
            await MeasureAsync("Capture on; normal host traffic; 2-second hint polling", 40, true, false);
            await MeasureAsync("Capture on; 20 direct UDP DNS questions, one every 2 seconds", 40, true, true);
            await ReadAsync();
            if (sent != 20 || answered == 0 || observedPolls == 0) throw new IOException("Live DNS workload or passive hostname matching was incomplete.");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            client.Dispose();
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                if ((await PassiveDnsSelfTest.SessionsAsync()).IsSubsetOf(before) && tracked.Skip(1).All(x => x.Process.HasExited)) { cleanup = true; break; }
            }
            foreach (var (_, process) in tracked.Skip(1)) process.Dispose();
        }
        static object Timing(List<double> source) { var a = source.Order().ToArray(); return new { Count = a.Length, MedianMs = a.Length == 0 ? 0 : a[a.Length / 2], P95Ms = a.Length == 0 ? 0 : a[(int)Math.Ceiling(a.Length * .95) - 1] }; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "domain-capture-benchmark.json"), JsonSerializer.Serialize(new {
            TimestampUtc = DateTimeOffset.UtcNow, Completed = failure is null, CleanupVerified = cleanup, StartupMs = startupMs,
            AssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ZikWall.dll")))) ,
            BrokerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ZikWall.Broker.dll")))) ,
            EngineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ZikWall.Engine.dll")))) ,
            LogicalProcessors = Environment.ProcessorCount, OS = Environment.OSVersion.ToString(),
            Scope = "Production DNS capture, broker and controller; diagnostic host without a window or monitoring plus diagnostic polling. Whole-app live UI is measured separately. Process CPU excludes kernel/system-wide ETW overhead and other applications. Network workload is 20 low-rate DNS attempts with a 5-second receive timeout, not NIC throughput or maximum capture capacity. Timeouts are recorded, not retried or excluded. No forced GC or working-set trimming in resource phases.",
            QueriesSent = sent, ValidReplies = answered, QueryTimeouts = timedOut, PollsMatchingObservedHostname = observedPolls,
            DnsRoundTrip = Timing(queryTimes), HintIpc = Timing(ipcTimes), LastStatus = lastStatus, Phases = phases, Failure = failure
        }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null && cleanup ? 0 : 1;
    }
}
