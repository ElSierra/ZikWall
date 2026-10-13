using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class PassiveDnsTests
{
    private static byte[] Name(string value) => value.Split('.').SelectMany(x => new[] { (byte)x.Length }.Concat(Encoding.ASCII.GetBytes(x))).Append((byte)0).ToArray();
    private static byte[] Reply(string name, ushort type, params (string Owner, ushort Type, uint Ttl, byte[] Data)[] records)
    {
        using var output = new MemoryStream();
        void Short(ushort value) { output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value); }
        Short(123); Short(0x8180); Short(1); Short((ushort)records.Length); Short(0); Short(0);
        output.Write(Name(name)); Short(type); Short(1);
        foreach (var r in records)
        {
            output.Write(r.Owner == name ? new byte[] { 0xc0, 12 } : Name(r.Owner));
            Short(r.Type); Short(1); Short((ushort)(r.Ttl >> 16)); Short((ushort)r.Ttl);
            Short((ushort)r.Data.Length); output.Write(r.Data);
        }
        return output.ToArray();
    }
    private static byte[] Packet(byte[] dns, bool ipv6 = false)
    {
        int ip = ipv6 ? 40 : 20;
        var packet = new byte[ip + 8 + dns.Length]; packet[0] = ipv6 ? (byte)0x60 : (byte)0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ipv6 ? 4 : 2), (ushort)(ipv6 ? 8 + dns.Length : packet.Length));
        packet[ipv6 ? 6 : 9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ip), 53);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ip + 2), 44000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ip + 4), (ushort)(dns.Length + 8));
        dns.CopyTo(packet, ip + 8); return packet;
    }
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        byte[] address = IPAddress.Parse("192.0.2.25").GetAddressBytes();
        var dns = Reply("www.example.com", 1, ("www.example.com", 1, 90, address));
        var answer = DnsResponseParser.Parse(dns).Single();
        Check(answer == new CachedDomainAnswer("192.0.2.25", "www.example.com", 90, true), "Compressed A record must preserve question, address, TTL and provenance.");
        var cname = Reply("www.example.com", 1, ("www.example.com", 5, 20, Name("cdn.example.net")), ("cdn.example.net", 1, 90, address), ("unrelated.example", 1, 90, IPAddress.Parse("192.0.2.99").GetAddressBytes()));
        Check(DnsResponseParser.Parse(cname).Single() == answer with { TtlSeconds = 20 }, "CNAME follows only question chain, retains original hostname and minimum TTL.");
        var v6dns = Reply("v6.example.com", 28, ("v6.example.com", 28, 45, IPAddress.Parse("2001:db8::25").GetAddressBytes()));
        Check(DnsResponseParser.Parse(v6dns).Single().Address == "2001:db8::25", "AAAA replies must decode.");
        Check(DnsResponseParser.Parse(Reply("www.example.com", 1, ("unrelated.example", 1, 90, address))).Count == 0, "Unrelated answer records cannot poison question mapping.");
        Check(DnsResponseParser.Parse(Reply("www.example.com", 1, ("www.example.com", 5, 20, Name("www.example.com")))).Count == 0, "CNAME cycles cannot publish names.");
        foreach (ushort flags in new ushort[] { 0x0180, 0x8380, 0x8183, 0x8980 })
        {
            var bad = dns.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(2), flags);
            Check(DnsResponseParser.Parse(bad).Count == 0, "Queries, truncated/error replies and nonstandard opcodes must be rejected.");
        }
        for (int length = 0; length < dns.Length; length++) Check(DnsResponseParser.Parse(dns.AsSpan(0, length)).Count == 0, "Every truncated reply must be safe and rejected.");
        var loop = dns.ToArray(); loop[12] = 0xc0; loop[13] = 12;
        Check(DnsResponseParser.Parse(loop).Count == 0, "Compression self-reference must fail.");
        var conflicting = Reply("www.example.com", 1, ("www.example.com", 5, 20, Name("a.example")), ("www.example.com", 5, 20, Name("b.example")), ("a.example", 1, 90, address));
        Check(DnsResponseParser.Parse(conflicting).Count == 0, "Conflicting CNAME records must be rejected.");
        foreach (bool ipv6 in new[] { false, true })
        {
            byte[] packet = Packet(dns, ipv6);
            Check(DnsPacketParser.Payload(packet, true).SequenceEqual(dns), "Complete IPv4/IPv6 UDP replies must decode.");
            for (int length = 0; length < packet.Length; length++) Check(DnsPacketParser.Payload(packet.AsSpan(0, length), true).IsEmpty, "Truncated IP/UDP packets must be rejected.");
            var ethernet = new byte[14 + packet.Length]; ethernet[12] = ipv6 ? (byte)0x86 : (byte)8; ethernet[13] = ipv6 ? (byte)0xdd : (byte)0; packet.CopyTo(ethernet, 14);
            Check(DnsPacketParser.Payload(ethernet).SequenceEqual(dns), "Ethernet framing must decode.");
            var vlan = new byte[ethernet.Length + 4]; ethernet.AsSpan(0, 12).CopyTo(vlan); vlan[12] = 0x81; ethernet.AsSpan(12).CopyTo(vlan.AsSpan(16));
            Check(DnsPacketParser.Payload(vlan).SequenceEqual(dns), "VLAN framing must decode.");
            packet[ipv6 ? 40 : 20] = 1;
            Check(DnsPacketParser.Payload(packet, true).IsEmpty, "Non-DNS source ports cannot be parsed as replies.");
        }
        var fragment = Packet(dns); fragment[6] = 0x20;
        byte[] wifi = new byte[26 + 8 + Packet(dns).Length];
        wifi[0] = 0x88; wifi[1] = 2; // QoS data, FromDS.
        new byte[] { 0xaa, 0xaa, 3, 0, 0, 0, 8, 0 }.CopyTo(wifi, 26); Packet(dns).CopyTo(wifi, 34);
        Check(DnsPacketParser.WifiPayload(wifi).SequenceEqual(dns), "Native Wi-Fi QoS DNS replies must decode through LLC/SNAP.");
        wifi[24] = 0x80; Check(DnsPacketParser.WifiPayload(wifi).IsEmpty, "Aggregated Wi-Fi payloads require separate decoding."); wifi[24] = 0;
        wifi[1] |= 4; Check(DnsPacketParser.WifiPayload(wifi).IsEmpty, "Fragmented Wi-Fi frames must not be guessed at."); wifi[1] &= 0xfb;
        wifi[26] = 0; Check(DnsPacketParser.WifiPayload(wifi).IsEmpty, "Encrypted or non-SNAP Wi-Fi payloads must be rejected.");
        Check(DnsPacketParser.Payload(fragment, true).IsEmpty, "IPv4 fragments require reassembly and must be rejected.");
        var v6 = Packet(dns, true); var extension = new byte[v6.Length + 8];
        v6.AsSpan(0, 40).CopyTo(extension); v6.AsSpan(40).CopyTo(extension.AsSpan(48)); extension[6] = 60; extension[40] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(extension.AsSpan(4), (ushort)(extension.Length - 40));
        Check(DnsPacketParser.Payload(extension, true).SequenceEqual(dns), "IPv6 extension headers must be traversed.");
        extension[6] = 44; extension[43] = 1;
        Check(DnsPacketParser.Payload(extension, true).IsEmpty, "IPv6 fragments must be rejected.");
        var random = new Random(42);
        for (int i = 0; i < 5000; i++) { var bytes = new byte[random.Next(0, 800)]; random.NextBytes(bytes); DnsResponseParser.Parse(bytes); DnsPacketParser.Payload(bytes, i % 2 == 0); }
        Check(true, "Malformed random packets must not throw.");
        long now = 0; var cache = new ObservedDnsCache(() => now); var wanted = new HashSet<string> { answer.Address };
        cache.Observe([answer]); now = 5000;
        Check(cache.Read(wanted).Single().TtlSeconds == 85, "Cache reads must not renew TTL.");
        now = 90000; Check(cache.Read(wanted).Count == 0, "Observed TTL must expire.");
        cache.Observe([answer with { TtlSeconds = uint.MaxValue }]); now += 3600000;
        Check(cache.Read(wanted).Count == 0, "Cache TTL must be capped at one hour.");
        cache.Observe([answer, answer with { TtlSeconds = 0 }]);
        Check(cache.Read(wanted).Count == 0, "TTL zero invalidates matching cached name.");
        cache.Observe([answer with { Address = "::ffff:0.0.0.0" }, answer with { Address = "::ffff:224.0.0.1" }]);
        Check(cache.Read(new HashSet<string> { "0.0.0.0", "224.0.0.1" }).Count == 0, "Mapped unspecified/multicast addresses must be rejected.");
        for (int i = 0; i < 5; i++) { now++; cache.Observe([answer with { Name = $"n{i}.example" }]); }
        Check(cache.Read(wanted).Select(x => x.Name).SequenceEqual(new[] { "n4.example", "n3.example", "n2.example" }), "Shared IP retains three most recently seen names.");
        var bounded = new ObservedDnsCache(() => now);
        for (int i = 0; i < ObservedDnsCache.Capacity + 1; i++) { now++; bounded.Observe([answer with { Name = $"n{i}.example", Address = $"10.0.{i / 256}.{i % 256}" }]); }
        Check(bounded.Read(new HashSet<string> { "10.0.0.0" }).Count == 0, "Capacity must evict oldest observation.");
        var eviction = new ObservedDnsCache(() => now);
        for (int i = 0; i < ObservedDnsCache.Capacity; i++)
            eviction.Observe([answer with { Name = $"evict{i}.example", Address = $"10.1.{i / 256}.{i % 256}", TtlSeconds = i == 1 ? 1u : 3600u }]);
        now += 1000;
        eviction.Observe([answer with { Name = "new.example", Address = "10.2.0.1" }]);
        Check(eviction.Read(new HashSet<string> { "10.1.0.0", "10.1.0.1", "10.2.0.1" }).Select(x => x.Name).ToHashSet().SetEquals(["evict0.example", "new.example"]),
            "A full cache must remove expired entries before evicting an older still-valid entry.");
        now++;
        eviction.Observe([answer with { Name = "evict0.example", Address = "10.1.0.0", TtlSeconds = 3600 }]);
        eviction.Observe([answer with { Name = "next.example", Address = "10.2.0.2" }]);
        Check(eviction.Read(new HashSet<string> { "10.1.0.0", "10.1.0.2" }).Single().Name == "evict0.example",
            "Refreshing an old name must preserve it and evict the least recently observed name, even with tied timestamps.");
        eviction.Observe([answer with { Name = "evict0.example", Address = "10.1.0.0", TtlSeconds = 0 }]);
        eviction.Observe([answer with { Name = "replacement.example", Address = "10.2.0.3" }]);
        Check(eviction.Read(new HashSet<string> { "10.1.0.0", "10.1.0.3", "10.2.0.3" }).Count == 2,
            "TTL-zero deletion must release capacity without evicting another valid name.");
        var renewed = new ObservedDnsCache(() => now);
        renewed.Observe([answer with { TtlSeconds = 1 }]); now += 500;
        renewed.Observe([answer with { TtlSeconds = 10 }]); now += 500;
        Check(renewed.Read(wanted).Single().Name == answer.Name, "An older expiration must not remove a renewed entry.");
        now += 10000;
        Check(renewed.Read(wanted).Count == 0, "A renewed entry must still expire at its latest deadline.");
        now = 0; int reads = 0;
        var row = new ConnectionEntry("TCPv4", "10.0.0.1", 40000, answer.Address, 443, 42, "Established");
        using var hints = new CachedDomainHints(_ => { reads++; return [answer, answer with { Name = "cache.example", ObservedResponse = false }]; }, () => now, true);
        hints.RequestRefresh([row], false); await hints.Pending;
        Check(hints.Find(answer.Address) == answer.Name && hints.IsObserved(answer.Address), "Observed response must take precedence over Windows cache.");
        now = 1999; hints.RequestRefresh([row], false); Check(reads == 1, "Live polls must throttle.");
        now = 2000; hints.RequestRefresh([row], false); await hints.Pending; Check(reads == 2, "Live replies must refresh in two seconds.");
        bool removed = false;
        using var invalidated = new CachedDomainHints(_ => removed ? [] : [answer], () => now, true);
        invalidated.RequestRefresh([row], false); await invalidated.Pending;
        removed = true; now += 2000; invalidated.RequestRefresh([row], false); await invalidated.Pending;
        Check(invalidated.Find(answer.Address) == "", "An authoritative empty capture snapshot must clear removed observations.");
        int heartbeats = 0;
        using var waitingCapture = new CachedDomainHints(_ => { heartbeats++; return []; }, () => now, true);
        waitingCapture.RequestRefresh([], false); await waitingCapture.Pending;
        Check(heartbeats == 1, "Enabled capture must remain alive while waiting for the first remote connection.");
        var named = row with { DomainHint = answer.Name, DomainHintObserved = true };
        Check(named.HostNameSource == "Observed DNS response" && named.Identity == row.Identity, "Provenance must be visible without changing connection identity.");
        Check(!JsonSerializer.Serialize(named).Contains(answer.Name) && !JsonSerializer.Serialize(named).Contains("DomainHintObserved"), "Names/provenance must not persist in saved activity.");
        Console.WriteLine($"PASS: {checks} passive DNS parser, transport, cache and enrichment checks.");
    }
}
