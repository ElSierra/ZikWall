using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Explicit diagnostic. No Internet traffic, saved policies, adapter settings or
// payload recording. All filters match this executable and loopback addresses.
internal static class ThroughputSelfTest
{
    private sealed record Sample(int Pair, string State, int Rules, double Mbps, double ConnectMs, long Bytes, double Seconds, double CpuMs);
    public static async Task RunAsync()
    {
        var samples = new List<Sample>();
        var checks = new List<string>();
        string? failure = null;
        bool cleanup = false;
        using var client = new FirewallClient();
        using var process = Process.GetCurrentProcess();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var guard = new TcpListener(IPAddress.Loopback, 0);
        int port = 0, guardPort = 0;
        try
        {
            listener.Start(); guard.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            guardPort = ((IPEndPoint)guard.LocalEndpoint).Port;
            async Task ApplyAsync(List<FirewallRule> rules)
            {
                var result = await client.SendAsync(new("apply", rules));
                if (!result.Success || result.ActiveRules != rules.Count) throw new IOException($"Policy was not accepted: {result.Message}");
            }
            async Task<bool> CanConnectGuardAsync()
            {
                using var socket = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, guardPort, timeout.Token);
                    using var accepted = await guard.AcceptTcpClientAsync(timeout.Token);
                    return true;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
            }
            if (!await CanConnectGuardAsync()) throw new IOException("Loopback baseline failed before any rule was installed.");
            checks.Add("Unfiltered guard-port baseline connected");
            // Warm the identical transfer path before comparing states.
            await TransferAsync(listener, port, -1, "warmup", 0, process);
            await client.StartAsync();
            checks.Add("Production unelevated client/elevated broker connected");
            foreach (int ruleCount in new[] { 2, 1000 })
            {
                var rules = new List<FirewallRule>
                {
                    new(Guid.NewGuid(), "Throughput guard", Environment.ProcessPath!, "127.0.0.1", 6, guardPort, 1, true, true, null)
                };
                for (int i = 0; i < ruleCount - 2; i++)
                    rules.Add(new(Guid.NewGuid(), $"Loopback fixture {i}", Environment.ProcessPath!, $"127.1.{i / 254}.{i % 254 + 1}", 6, port, 3, true, true, null));
                rules.Add(new(Guid.NewGuid(), "Throughput allowed transfer", Environment.ProcessPath!, "127.0.0.1", 6, port, 1, false, true, null));
                await ApplyAsync(rules);
                if (await CanConnectGuardAsync()) throw new IOException("Guard block failed: this would not be a filtered measurement.");
                checks.Add($"{ruleCount}-rule policy accepted and guard block verified");
                for (int pair = 0; pair < 5; pair++)
                {
                    // Alternate order to reduce consistent warmup/thermal/time bias.
                    foreach (bool enabled in pair % 2 == 0 ? new[] { false, true } : new[] { true, false })
                    {
                        await ApplyAsync(enabled ? rules : []);
                        samples.Add(await TransferAsync(listener, port, pair, enabled ? "on" : "off", ruleCount, process));
                    }
                }
            }
            await ApplyAsync([]);
            if (!await CanConnectGuardAsync()) throw new IOException("Empty policy did not restore the guard connection.");
            checks.Add("Empty policy restored guard connection");
            client.Dispose();
            cleanup = await CanConnectGuardAsync();
            if (!cleanup) throw new IOException("Post-session loopback connectivity failed.");
            checks.Add("Broker disconnected; loopback connectivity remains available");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally { client.Dispose(); listener.Stop(); guard.Stop(); }
        static double Median(IEnumerable<double> source) { var a = source.Order().ToArray(); return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
        var comparisons = samples.GroupBy(x => x.Rules).Select(group =>
        {
            var pairs = group.GroupBy(x => x.Pair).Where(x => x.Count() == 2).Select(x => x.Single(s => s.State == "on").Mbps / x.Single(s => s.State == "off").Mbps).ToArray();
            return new { Rules = group.Key, CompletePairs = pairs.Length, OffMedianMbps = Median(group.Where(x => x.State == "off").Select(x => x.Mbps)),
                OnMedianMbps = Median(group.Where(x => x.State == "on").Select(x => x.Mbps)), MedianPairedRatio = Median(pairs),
                MinimumPairedRatio = pairs.Length == 0 ? 0 : pairs.Min(), MaximumPairedRatio = pairs.Length == 0 ? 0 : pairs.Max() };
        }).ToArray();
        bool budgetPassed = failure is null && cleanup && comparisons.Length == 2 && comparisons.All(x => x.CompletePairs == 5 && x.MedianPairedRatio >= 0.95);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "throughput-selftest.json"), JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTimeOffset.UtcNow, Completed = failure is null, ThroughputBudgetPassed = budgetPassed,
            Scenario = "IPv4 TCP loopback; two-second unidirectional transfers with fresh connections; five alternating off/on pairs per rule count; production broker; no saved policy; no Internet or disk transfer",
            Interpretation = "Off means zero ZikWall filters with Windows Firewall unchanged. On includes a matching permit and a verified guard block. A median paired ratio below 0.95 flags investigation; passing does not prove zero overhead, Internet/VPN speed, UDP/IPv6 throughput or monitoring/ETW overhead.",
            AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ZikWall.dll")))),
            Port = port, GuardPort = guardPort, CleanupVerified = cleanup, Checks = checks, Comparisons = comparisons, Samples = samples, Failure = failure
        }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = budgetPassed ? 0 : 1;
    }

    private static async Task<Sample> TransferAsync(TcpListener listener, int port, int pair, string state, int rules, Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sender = new TcpClient { NoDelay = true };
        var connect = Stopwatch.StartNew();
        await sender.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        using var receiver = await listener.AcceptTcpClientAsync(deadline.Token);
        double connectMs = connect.Elapsed.TotalMilliseconds;
        byte[] send = new byte[64 * 1024], receive = new byte[64 * 1024];
        Array.Fill(send, (byte)0x5a);
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        long sent = 0, received = 0;
        var read = Task.Run(async () =>
        {
            int size;
            while ((size = await receiver.GetStream().ReadAsync(receive, deadline.Token)) != 0)
            {
                if (receive.AsSpan(0, size).IndexOfAnyExcept((byte)0x5a) >= 0) throw new IOException("Transfer payload mismatch.");
                received += size;
            }
        }, deadline.Token);
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            await sender.GetStream().WriteAsync(send, deadline.Token);
            sent += send.Length;
        }
        sender.Client.Shutdown(SocketShutdown.Send);
        await read;
        watch.Stop();
        if (received != sent || sent == 0) throw new IOException("Incomplete TCP transfer.");
        return new(pair, state, rules, received * 8 / watch.Elapsed.TotalSeconds / 1_000_000, connectMs, received,
            watch.Elapsed.TotalSeconds, (process.TotalProcessorTime - cpu).TotalMilliseconds);
    }
}
