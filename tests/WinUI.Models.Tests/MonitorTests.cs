using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class MonitorTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
        var v4 = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(v4, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(v4.AsSpan(4), 5);
        new byte[] { 127, 0, 0, 1 }.CopyTo(v4, 8);
        BinaryPrimitives.WriteUInt16BigEndian(v4.AsSpan(12), 51337);
        new byte[] { 192, 0, 2, 8 }.CopyTo(v4, 16);
        BinaryPrimitives.WriteUInt16BigEndian(v4.AsSpan(20), 443);
        BinaryPrimitives.WriteUInt32LittleEndian(v4.AsSpan(24), 1234);
        var entry = ConnectionMonitor.Parse(v4, true, false).Single();
        Check(entry.LocalEndpoint == "127.0.0.1:51337" && entry.RemoteEndpoint == "192.0.2.8:443" && entry.ProcessId == 1234 && entry.State == "Established", "TCPv4 address/port/PID byte order is wrong.");
        BinaryPrimitives.WriteUInt32LittleEndian(v4.AsSpan(4), 2);
        Check(ConnectionMonitor.Parse(v4, true, false).Single().State == "Listen", "Documented Windows listener state must decode correctly.");
        var v6 = new byte[60];
        BinaryPrimitives.WriteUInt32LittleEndian(v6, 1);
        IPAddress.Parse("fe80::1234").GetAddressBytes().CopyTo(v6, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(v6.AsSpan(20), 7);
        BinaryPrimitives.WriteUInt16BigEndian(v6.AsSpan(24), 50000);
        IPAddress.Parse("2001:db8::9").GetAddressBytes().CopyTo(v6, 28);
        BinaryPrimitives.WriteUInt16BigEndian(v6.AsSpan(48), 8443);
        BinaryPrimitives.WriteUInt32LittleEndian(v6.AsSpan(52), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(v6.AsSpan(56), 2345);
        var ipv6 = ConnectionMonitor.Parse(v6, true, true).Single();
        Check(ipv6.LocalEndpoint == "[fe80::1234%7]:50000" && ipv6.RemoteEndpoint == "[2001:db8::9]:8443" && ipv6.ProcessId == 2345, "TCPv6 layout or scope IDs are wrong.");
        var udp = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(udp, 1);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(8), 5353);
        Check(ConnectionMonitor.Parse(udp, false, false).Single() is { LocalPort: 5353, RemotePort: null, RemoteAddress: "" }, "UDP must not invent a destination.");
        try { ConnectionMonitor.Parse(v4[..^1], true, false); throw new Exception("Truncated table was accepted."); } catch (InvalidDataException) { checks++; }
        BinaryPrimitives.WriteUInt32LittleEndian(v4, uint.MaxValue);
        try { ConnectionMonitor.Parse(v4, true, false); throw new Exception("Unbounded table was accepted."); } catch (InvalidDataException) { checks++; }
        var now = DateTimeOffset.UtcNow;
        var tracker = new ConnectionTracker();
        Check(tracker.Observe([entry], now).Single().Event == "First seen", "Initial snapshots must not claim connection creation time.");
        Check(tracker.Observe([entry], now.AddSeconds(2)).Count == 0, "Unchanged connections must not create history noise.");
        Check(tracker.Observe([entry with { State = "CloseWait" }], now.AddSeconds(4)).Single().Event.Contains("CloseWait"), "State changes must be recorded.");
        Check(tracker.Observe([], now.AddSeconds(6)).Single().Event == "No longer observed", "Disappearing entries must be recorded without claiming a firewall block.");
        Check(tracker.Observe([entry, entry], now).Count == 1, "Duplicate rows must produce only one first-seen event.");
        Check(tracker.Observe([entry with { ProcessId = entry.ProcessId + 1 }], now).Count == 2, "Ownership changes must produce a disappearance and a new observation.");
        Check(entry.Matches("") && entry.Matches("192.0.2.8:443") && entry.Matches("TCPV4") && !entry.Matches("missing-value"), "Allocation-reduced search must preserve endpoint and case-insensitive matches.");
        var retained = new ActivityArchive([new(now, "keep", entry), new(now.AddDays(-8), "old", entry)], []);
        var copied = ActivityStore.Trim(retained, now);
        Check(copied.Events.Count == 1 && retained.Events.Count == 2, "Copy-returning trim must preserve its caller's archive.");
        var sameList = retained.Events;
        ActivityStore.TrimInPlace(retained, now);
        Check(ReferenceEquals(sameList, retained.Events) && retained.Events.Count == 1, "In-place trim must handle unsorted history without copying its retained list.");
        retained.Events.AddRange(Enumerable.Range(0, ActivityStore.MaximumEvents).Select(i => new ConnectionEvent(now, "fixture", entry)));
        ActivityStore.TrimInPlace(retained, now);
        Check(retained.Events.Count == ActivityStore.MaximumEvents && retained.Events[0].Event == "fixture", "In-place trim must retain only the newest bounded entries.");
        var collection = new ConnectionCollection();
        int notifications = 0, resets = 0;
        collection.CollectionChanged += (_, args) => { notifications++; if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        var fixtures = Enumerable.Range(0, 1000).Select(i => entry with { LocalPort = i }).ToArray();
        collection.Update(fixtures); notifications = 0; resets = 0;
        collection.Update(fixtures);
        Check(notifications == 0, "Unchanged snapshots must not notify/rebuild native list items.");
        var changed = fixtures.ToArray(); changed[5] = changed[5] with { State = "CloseWait" };
        collection.Update(changed);
        Check(notifications == 1 && resets == 0 && collection.SequenceEqual(changed), "One changed connection must replace only one row.");
        collection.Update([changed[^1], .. changed[..^1]]);
        Check(resets == 0 && collection[0] == changed[^1], "A small reorder must preserve native list items.");
        collection.Update(changed.Reverse().ToArray());
        Check(collection.SequenceEqual(changed.Reverse()) && resets > 0, "Large reorders must remain correct with bounded incremental work.");
        collection.Update([entry, entry]);
        Check(collection.Count == 2, "Duplicate identities must use a safe reset path.");
        var serialized = System.Text.Json.JsonSerializer.Serialize(new ConnectionEvent(now, "fixture", entry));
        Check(!serialized.Contains("LocalEndpoint") && !serialized.Contains("Identity") && System.Text.Json.JsonSerializer.Deserialize<ConnectionEvent>(serialized)?.Connection == entry, "History JSON must round-trip without allocating/repeating computed display fields.");
        var rates = new TrafficTracker();
        Check(rates.Observe([new("a", "Adapter A", 1000, 500)], now).Count == 0, "A single counter snapshot cannot establish a rate.");
        var rate = rates.Observe([new("a", "Adapter A", 5000, 1500)], now.AddSeconds(2)).Single();
        Check(rate.Download == 2000 && rate.Upload == 500, "Rates must use actual elapsed time.");
        Check(rates.Observe([new("a", "Adapter A", 0, 0)], now.AddSeconds(4)).Count == 0, "Counter resets must not produce negative or enormous rates.");
        string directory = Path.Combine(Path.GetTempPath(), "ZikWall-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ActivityStore(directory);
            store.Save(new([new(now.AddDays(-8), "old", entry), new(now, "First seen", entry)], [rate]));
            Check(store.Load().Events.Count == 1, "Activity persistence must round-trip and enforce retention.");
            store.Clear(); Check(!File.Exists(store.FilePath), "Clear must remove the saved activity file.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        if (OperatingSystem.IsWindows())
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            using var accepted = listener.AcceptTcpClient();
            using var udpSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var actual = ConnectionMonitor.Capture();
            // IP Helper tables can lag socket creation. Allow a bounded publication delay.
            for (int attempt = 0; attempt < 20 && !actual.Any(x => x.ProcessId == Environment.ProcessId && x.RemotePort == port); attempt++)
            { Thread.Sleep(100); actual = ConnectionMonitor.Capture(); }
            // Some host networking providers report state 0 for listening rows. Preserve Unknown
            // rather than inventing Listen; endpoint ownership must still agree with our fixture.
            Check(actual.Any(x => x.ProcessId == Environment.ProcessId && x.LocalPort == port && x.RemotePort == 0), "Live Windows monitor must find this process's bound endpoint.");
            Check(actual.Any(x => x.ProcessId == Environment.ProcessId && x.RemotePort == port && x.LocalPort == ((IPEndPoint)client.Client.LocalEndPoint!).Port), "Live Windows monitor must find the actual remote port and owning PID.");
            Check(actual.Any(x => x.ProcessId == Environment.ProcessId && x.Protocol == "UDPv4" && x.LocalPort == ((IPEndPoint)udpSocket.Client.LocalEndPoint!).Port && x.RemotePort == null), "Live UDP capture must identify this process's local endpoint.");
            Check(actual.Any(x => x.ProcessId == Environment.ProcessId && string.Equals(x.ProcessPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)), "Limited-information process query must resolve this executable's real path.");
            var outgoing = actual.First(x => x.ProcessId == Environment.ProcessId && x.RemotePort == port && x.LocalPort == ((IPEndPoint)client.Client.LocalEndPoint!).Port);
            var incoming = actual.First(x => x.ProcessId == Environment.ProcessId && x.LocalPort == port && x.RemotePort == outgoing.LocalPort);
            Check(TcpDirection.Read(outgoing) == "Outgoing" && TcpDirection.Read(incoming) == "Incoming", "Windows SYN options must distinguish initiating and accepted TCP endpoints.");
            Console.WriteLine("Live Windows TCP/UDP loopback integration passed.");
        }
        Console.WriteLine($"PASS: {checks} monitoring and persistence checks.");
        return checks;
    }
}
