using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Explicit read-only diagnostic of the provider and isolated elevated helper.
public static class DomainCacheSelfTest
{
    public static async Task RunAsync()
    {
        var result = new Dictionary<string, object>();
        using var client = new FirewallClient();
        try
        {
            var addresses = ConnectionMonitor.Capture().Where(x => x.RemotePort > 0).Select(x => x.RemoteAddress)
                .Concat(new[] { "0.0.0.0", "::", "127.0.0.1", "::1" }).Distinct().Take(CachedDomainHints.MaxAddresses).ToList();
            var cache = new WindowsDnsCache();
            int found = 0;
            var wanted = addresses.ToHashSet();
            var scanTimes = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var scan = Stopwatch.StartNew();
                found += cache.Read(wanted).Count;
                scanTimes.Add(scan.Elapsed.TotalMilliseconds);
            }
            result["DirectHintsAcross5Scans"] = found;
            result["MeanProviderScanMs"] = scanTimes.Average();
            result["MaxProviderScanMs"] = scanTimes.Max();
            var activeOnly = ConnectionMonitor.Capture().Where(x => x.RemotePort > 0).Select(x => x.RemoteAddress).ToHashSet();
            var activeTimes = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var scan = Stopwatch.StartNew(); cache.Read(activeOnly);
                activeTimes.Add(scan.Elapsed.TotalMilliseconds);
            }
            result["ActiveRemoteIPs"] = activeOnly.Count;
            result["MeanActiveScanMs"] = activeTimes.Average();
            result["MaxActiveScanMs"] = activeTimes.Max();
            await client.StartAsync(domainsOnly: true);
            var prohibited = await client.SendAsync(new("apply", []));
            if (prohibited.Success) throw new InvalidDataException("Read-only helper accepted a firewall command.");
            var invalid = await client.SendAsync(new("domains", RemoteAddresses: ["example.com"]));
            if (invalid.Success) throw new InvalidDataException("Helper accepted a hostname instead of an IP.");
            var watch = Stopwatch.StartNew();
            var sample = await client.SendAsync(new("domains", RemoteAddresses: addresses));
            result["ReadMs"] = watch.Elapsed.TotalMilliseconds;
            if (!sample.Success || sample.Domains is null) throw new IOException("Cache reader failed: " + sample.Message);
            if (sample.Domains.Any(x => !addresses.Contains(x.Address))) throw new IOException("Unexpected address returned.");
            var throttled = await client.SendAsync(new("domains", RemoteAddresses: addresses));
            if (throttled.Domains?.Count != 0) throw new IOException("Cache scan rate limit failed.");
            result["Passed"] = true;
            result["MatchedHints"] = sample.Domains.Count;
            result["FirewallCommandsRejected"] = true;
            result["InvalidAddressesRejected"] = true;
        }
        catch (Exception ex) { result["Error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "domain-cache-selftest.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
