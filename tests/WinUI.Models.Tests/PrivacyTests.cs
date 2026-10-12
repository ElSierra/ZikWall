using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class PrivacyTests
{
    public static async Task TestDohAsync()
    {
        byte[] query = [0x4a, 0x21, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
        using var proxy = new DnsProxy(new Uri("https://cloudflare-dns.com/dns-query"), IPAddress.Parse("1.1.1.1"), 0);
        using var client = new UdpClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.SendAsync(query, new IPEndPoint(IPAddress.Loopback, proxy.LocalPort), timeout.Token);
        var response = (await client.ReceiveAsync(timeout.Token)).Buffer;
        if ((response[3] & 15) != 0 || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)) == 0 || proxy.Counts.Errors != 0) throw new Exception("Public DoH forwarding test failed.");
        Console.WriteLine("PASS: example.com resolved through the local UDP proxy and certificate-validated Cloudflare HTTPS transport.");
    }
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
        var list = BlocklistParser.Parse("# test\n0.0.0.0 ads.example trackers.example\n||ads.example^\n192.0.2.0/24\n2001:db8::/32\n@@||allow.example^\n0.0.0.0/0");
        Check(list.Domains.Count == 2 && list.Networks.Count == 2 && list.UnsupportedLines == 2, "Blocklist formats, deduplication and global-block rejection failed.");
        Check(BlocklistParser.Domain("BÜCHER.example.") == "xn--bcher-kva.example", "Domain normalization must use IDNA.");
        Check(BlocklistParser.Domain("https://ads.example/path") is null && BlocklistParser.Domain("-bad.example") is null, "URLs and invalid labels must not become domain rules.");
        var blocked = list.Domains.ToHashSet(); var allowed = new HashSet<string> { "safe.ads.example" };
        Check(BlocklistParser.MatchesDomain("a.ads.example", blocked, allowed) && !BlocklistParser.MatchesDomain("notads.example", blocked, allowed) && !BlocklistParser.MatchesDomain("x.safe.ads.example", blocked, allowed), "Domain boundaries or exceptions are wrong.");
        using (var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("network,latitude,longitude,label\n192.0.2.0/24,10,20,Wide\n192.0.2.128/25,11,21,Specific\n2001:db8::/32,-12,-34,IPv6\n")))
        {
            var geo = GeoDatabase.Read(data);
            Check(geo.Find("192.0.2.200")?.Label == "Specific" && geo.Find("192.0.2.1")?.Label == "Wide", "Map lookup must prefer the longest matching network.");
            Check(geo.Find("2001:db8::123")?.Longitude == -34 && geo.Find("198.51.100.1") is null, "Map lookup must support IPv6 and preserve unknown locations.");
        }
        using (var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("network,latitude,longitude,label\n192.0.2.0/24,NaN,20,Bad\n")))
            try { GeoDatabase.Read(data); throw new Exception("Invalid map coordinates accepted."); } catch (InvalidDataException) { checks++; }
        using (var oversized = new MemoryStream(new byte[11]))
            try { await BlocklistService.ReadBoundedAsync(oversized, 10, CancellationToken.None); throw new Exception("Unbounded response accepted."); } catch (InvalidDataException) { checks++; }
        byte[] query = [0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, (byte)'a', (byte)'d', (byte)'s', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 0, 0, 1, 0, 1];
        Check(DnsMessage.Question(query).Name == "ads.example", "DNS question parsing failed.");
        var blockedAnswer = DnsMessage.Error(query, 3);
        Check(blockedAnswer[0] == 0x12 && blockedAnswer[1] == 0x34 && (blockedAnswer[2] & 128) != 0 && (blockedAnswer[3] & 15) == 3, "NXDOMAIN must preserve transaction ID and question.");
        var malformed = (byte[])query.Clone(); malformed[12] = 0xc0;
        try { DnsMessage.Question(malformed); throw new Exception("Compression loop accepted."); } catch (InvalidDataException) { checks++; }
        using (var proxy = new DnsProxy(new Uri("https://resolver.invalid/dns-query"), IPAddress.Loopback, 0))
        {
            proxy.SetLists(list.Domains, []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var udp = new UdpClient();
            await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, proxy.LocalPort), timeout.Token);
            var response = await udp.ReceiveAsync(timeout.Token);
            Check(response.Buffer.SequenceEqual(blockedAnswer), "UDP DNS blocking failed.");
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, proxy.LocalPort, timeout.Token);
            var stream = tcp.GetStream(); var size = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)query.Length);
            await stream.WriteAsync(size, timeout.Token); await stream.WriteAsync(query, timeout.Token);
            await stream.ReadExactlyAsync(size, timeout.Token); var answer = new byte[BinaryPrimitives.ReadUInt16BigEndian(size)]; await stream.ReadExactlyAsync(answer, timeout.Token);
            Check(answer.SequenceEqual(blockedAnswer) && proxy.Counts.Blocked == 2, "TCP DNS blocking and counters failed.");
        }
        Console.WriteLine($"PASS: {checks} subscription/parser and real UDP/TCP DNS tests (no public DNS queries sent).");
        if (OperatingSystem.IsWindows())
        {
            var identity = await ExecutableIdentity.InspectAsync(Environment.ProcessPath!);
            if (identity.Sha256.Length != 64) throw new Exception("Executable identity hashing failed.");
            Console.WriteLine("PASS: local executable SHA-256 and offline Windows trust inspection completed.");
        }
    }
}
