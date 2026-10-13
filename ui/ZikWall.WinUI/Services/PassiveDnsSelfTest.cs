using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Explicit diagnostic only. Sends one unique ordinary DNS question directly to
// Cloudflare, bypassing the OS resolver cache; never changes adapter DNS settings.
public static class PassiveDnsSelfTest
{
    public static async Task RunAsync()
    {
        var report = new Dictionary<string, object>();
        using var client = new FirewallClient();
        var before = await SessionsAsync();
        try
        {
            await client.StartAsync(domainsOnly: true);
            BrokerResponse status = new(true, "Starting");
            for (int i = 0; i < 50; i++)
            {
                status = await client.SendAsync(new("status"));
                if (!status.Message.StartsWith("Starting", StringComparison.Ordinal)) break;
                await Task.Delay(500);
            }
            report["CaptureStatus"] = status.Message;
            if (!status.Success || status.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || status.Message.StartsWith("Starting", StringComparison.Ordinal)) throw new IOException(status.Message);
            string name = "zikw-" + Guid.NewGuid().ToString("N")[..12] + ".1-0-0-1.sslip.io";
            byte[] query = Query(name);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(IPAddress.Parse("1.1.1.1"), 53);
            await udp.SendAsync(query);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var reply = await udp.ReceiveAsync(timeout.Token);
            if (!reply.Buffer.AsSpan(0, 2).SequenceEqual(query.AsSpan(0, 2)) || !DnsResponseParser.Parse(reply.Buffer).Any(x => x.Address == "1.0.0.1" && x.Name == name))
                throw new IOException("The diagnostic DNS resolver did not return the expected test address.");
            report["DirectUdpReplyVerified"] = true;
            List<CachedDomainAnswer> answers = [];
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++)
            {
                var response = await client.SendAsync(new("domains", RemoteAddresses: ["1.0.0.1"]));
                if (!response.Success) throw new IOException(response.Message);
                report["CaptureStatus"] = response.Message;
                answers = response.Domains ?? [];
                if (answers.Any(x => x.ObservedResponse && x.Name == name && x.Address == "1.0.0.1" && x.TtlSeconds > 0)) break;
                await Task.Delay(500);
            }
            if (!answers.Any(x => x.ObservedResponse && x.Name == name)) throw new IOException("DNS reply reached the app, but passive capture did not learn its hostname.");
            report["ObservedReplyMatched"] = true;
            report["CaptureToHintMs"] = watch.Elapsed.TotalMilliseconds;
            using var enrichment = new MonitorEnrichment(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries"));
            enrichment.SetDomainReader(_ => answers);
            var row = new ConnectionEntry("TCPv4", "192.0.2.1", 40000, "1.0.0.1", 443, Environment.ProcessId, "Established");
            for (int i = 0; i < 20 && enrichment.WithDomainHint(row).DomainHint.Length == 0; i++) { enrichment.Enrich([row]); await Task.Delay(50); }
            var named = enrichment.WithDomainHint(row);
            if (!named.DomainHint.Contains(name, StringComparison.Ordinal) || named.HostNameSource != "Observed DNS response" || named.Identity != row.Identity)
                throw new IOException("Observed hostname did not reach connection enrichment.");
            report["ConnectionEnrichmentVerified"] = true;
            if ((await client.SendAsync(new("apply", []))).Success || (await client.SendAsync(new("domains", RemoteAddresses: ["example.com"]))).Success)
                throw new IOException("Domain helper request validation failed.");
            report["FirewallAndInvalidAddressRequestsRejected"] = true;
            report["Passed"] = true;
        }
        catch (Exception ex) { report["Error"] = ex.ToString(); }
        finally
        {
            client.Dispose();
            HashSet<string> after = [];
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500); after = await SessionsAsync();
                if (after.IsSubsetOf(before)) break;
            }
            report["OwnedTraceStopped"] = after.IsSubsetOf(before);
            if (!after.IsSubsetOf(before)) { report["Passed"] = false; report["CleanupError"] = "A new ZikWall DNS trace remained after disconnect."; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "passive-dns-selftest.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    internal static byte[] Query(string name)
    {
        using var output = new MemoryStream();
        byte[] header = new byte[12]; Random.Shared.NextBytes(header.AsSpan(0, 2)); header[2] = 1; header[5] = 1; output.Write(header);
        foreach (string label in name.Split('.')) { output.WriteByte((byte)label.Length); output.Write(Encoding.ASCII.GetBytes(label)); }
        output.Write(new byte[] { 0, 0, 1, 0, 1 }); return output.ToArray();
    }
    internal static async Task<HashSet<string>> SessionsAsync()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "logman.exe"), "query -ets")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }) ?? throw new IOException("Could not inspect trace cleanup.");
        string output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Administrator access is needed to verify trace cleanup.");
        return output.Split('\n').Where(x => x.TrimStart().StartsWith("ZikWall.Dns.", StringComparison.Ordinal)).Select(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToHashSet(StringComparer.Ordinal);
    }
}
