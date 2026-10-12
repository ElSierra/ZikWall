using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class MonitorEnrichmentTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var now = DateTimeOffset.UtcNow;
        var row = new ConnectionEntry("TCPv4", "192.168.1.2", 51234, "8.8.8.8", 443, 1, "Established", "fixture", "", "Outgoing", "US", "United States", "Internet");
        foreach (var ip in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1" }) Check(NetworkScope.Classify(ip) == "Loopback", "Loopback scope: " + ip);
        foreach (var ip in new[] { "192.168.1.1", "10.0.0.1", "172.31.0.1", "169.254.1.1", "100.64.0.1", "fe80::1%4", "fd01::1" }) Check(NetworkScope.Classify(ip) == "Local network", "Local scope: " + ip);
        foreach (var ip in new[] { "0.0.0.0", "224.0.0.1", "192.0.2.1", "198.51.100.1", "203.0.113.1", "2001:db8::1", "3fff::1", "::2", "::" }) Check(NetworkScope.Classify(ip) == "Reserved", "Reserved scope: " + ip);
        Check(NetworkScope.Classify("8.8.8.8") == "Internet" && NetworkScope.Classify("2606:4700:4700::1111") == "Internet" && NetworkScope.Classify("") == "Unknown", "Public/unknown address classification.");
        Check(MonitorFilter.Matches(row, "United", 1, 1) && !MonitorFilter.Matches(row, "", 1, 2) && !MonitorFilter.Matches(row, "", 2, 0), "Direction/scope/search filters must agree.");
        Check(TcpDirection.Read(row with { Protocol = "UDPv4", RemotePort = null }) == "Unknown" && TcpDirection.Read(row with { RemotePort = 0 }) == "Listening", "UDP and listeners must not invent directions.");
        var alerts = new RecentConnections();
        ConnectionEvent Seen(ConnectionEntry item) => new(now, "First seen", item);
        Check(alerts.Observe([Seen(row)], now, true) is null, "Startup must not generate an alert storm.");
        Check(alerts.Observe([Seen(row with { NetworkScope = "Local network" })], now, true) is null, "Local endpoints must not generate Internet alerts.");
        Check(alerts.Observe([Seen(row)], now.AddSeconds(1), true)?.Count == 1, "New Internet endpoints should alert.");
        Check(alerts.Observe([Seen(row)], now.AddSeconds(31), true) is null, "Repeated endpoints must be deduplicated.");
        Check(alerts.Observe([Seen(row with { RemotePort = 80 })], now.AddSeconds(2), true) is null, "Bursts should wait for the cooldown.");
        Check(alerts.Observe([], now.AddSeconds(32), true)?.Connections.Single().RemotePort == 80, "Queued alerts must flush without another new endpoint.");
        alerts.ResetBaseline();
        Check(alerts.Observe([Seen(row)], now.AddMinutes(10), true) is null, "Resume/toggle must suppress its baseline.");
        Check(alerts.Observe([Seen(row)], now.AddMinutes(11), false) is null, "Disabled alerts must stay quiet.");
        var oldJson = "{\"Protocol\":\"TCPv4\",\"LocalAddress\":\"127.0.0.1\",\"LocalPort\":1,\"RemoteAddress\":\"8.8.8.8\",\"RemotePort\":443,\"ProcessId\":1,\"State\":\"Established\"}";
        Check(JsonSerializer.Deserialize<ConnectionEntry>(oldJson) is { Direction: "Unknown", CountryCode: "", NetworkScope: "Unknown" }, "Old saved history must retain safe defaults.");
        string data = Path.GetFullPath("ui/ZikWall.WinUI/Assets/Countries");
        using (var countries = new CountryDatabase(data))
        {
            Check(countries.Find("8.8.8.8").Code == "US" && countries.Find("2001:4860:4860::8888").Code == "CA", "Lookups must match the pinned CSV ranges (anycast locations are approximate).");
            Check(countries.Find("::ffff:8.8.8.8") == countries.Find("8.8.8.8") && countries.Find("invalid").Code == "", "Mapped IPv4 and invalid addresses.");
            // Every dataset country needs a real local flag, including non-ISO regions.
            foreach (var (file, width) in new[] { ("ipv4.zwc", 4), ("ipv6.zwc", 16) })
            {
                using var reader = new BinaryReader(File.OpenRead(Path.Combine(data, file)));
                reader.ReadUInt32(); int count = reader.ReadInt32();
                var codes = new HashSet<string>();
                for (int i = 0; i < count; i++) { reader.ReadBytes(width * 2); codes.Add(new string(reader.ReadChars(2))); }
                Check(codes.All(code => code is "ZZ" or "XX" || File.Exists(Path.Combine(data, "../Flags", code.ToLowerInvariant() + ".svg"))), "Missing country flag for " + file);
            }
        }
        string fixture = Path.Combine(Path.GetTempPath(), "ZikWall-country-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            foreach (var (name, width) in new[] { ("ipv4.zwc", 4), ("ipv6.zwc", 16) })
            {
                using var writer = new BinaryWriter(File.Create(Path.Combine(fixture, name)));
                writer.Write(0x3143575au); writer.Write(1);
                var first = new byte[width]; first[^1] = 10; var last = new byte[width]; last[^1] = 20;
                writer.Write(first); writer.Write(last); writer.Write(new byte[] { 85, 83 });
            }
            using (var countries = new CountryDatabase(fixture))
                Check(countries.Find("0.0.0.10").Code == "US" && countries.Find("0.0.0.20").Code == "US" && countries.Find("0.0.0.9").Code == "" && countries.Find("0.0.0.21").Code == "" && countries.Find("::a").Code == "US" && countries.Find("::14").Code == "US" && countries.Find("::15").Code == "", "Country range boundaries must be inclusive without matching gaps.");
            File.WriteAllBytes(Path.Combine(fixture, "ipv4.zwc"), new byte[8]);
            try { using var invalid = new CountryDatabase(fixture); throw new Exception("Invalid header accepted."); } catch (InvalidDataException) { checks++; }
            using var degraded = new MonitorEnrichment(fixture);
            var preserved = degraded.Enrich([row with { Protocol = "UDPv4" }]).Single();
            Check(preserved.CountryCode == "" && preserved.RemoteAddress == row.RemoteAddress && degraded.CountryDataStatus.Contains("unavailable"), "Missing/corrupt countries must not stop connection monitoring.");
        }
        finally { Directory.Delete(fixture, true); }
        if (OperatingSystem.IsWindows() && Socket.OSSupportsIPv6)
        {
            using var listener = new TcpListener(IPAddress.IPv6Loopback, 0); listener.Start();
            using var client = new TcpClient(AddressFamily.InterNetworkV6); client.Connect((IPEndPoint)listener.LocalEndpoint);
            using var accepted = listener.AcceptTcpClient();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port, localPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            var rows = ConnectionMonitor.Capture();
            for (int i = 0; i < 20 && !rows.Any(x => x.ProcessId == Environment.ProcessId && x.RemotePort == port); i++) { Thread.Sleep(100); rows = ConnectionMonitor.Capture(); }
            var outgoing = rows.Single(x => x.Protocol == "TCPv6" && x.ProcessId == Environment.ProcessId && x.LocalPort == localPort && x.RemotePort == port);
            var incoming = rows.Single(x => x.Protocol == "TCPv6" && x.ProcessId == Environment.ProcessId && x.LocalPort == port && x.RemotePort == localPort);
            Check(TcpDirection.Read(outgoing) == "Outgoing" && TcpDirection.Read(incoming) == "Incoming", "Real IPv6 SYN direction must match initiating and accepting sockets.");
        }
        Console.WriteLine($"PASS: {checks} country, direction, filter and notification checks.");
    }
}
