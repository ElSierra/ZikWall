using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Explicit diagnostic: loopback only, ephemeral ports, no filters/DNS changes.
public static class UsageSelfTest
{
    public static async Task RunAsync()
    {
        var results = new Dictionary<string, object>();
        using var client = new FirewallClient();
        try
        {
            await client.StartAsync();
            var start = await client.SendAsync(new("usage-start"));
            if (!start.Success) throw new IOException(start.Message);
            async Task<AppTraffic> Sample()
            {
                await Task.Delay(2200); // ETW's bounded real-time buffer delivery.
                var result = await client.SendAsync(new("usage"));
                if (!result.Success || result.Usage is null) throw new IOException(result.Message);
                results["TraceBufferKiB"] = result.Usage.BufferKiB;
                results["LostEvents"] = result.Usage.LostEvents;
                results["UnattributedBytes"] = result.Usage.UnattributedBytes;
                return result.Usage.Apps.FirstOrDefault(x => string.Equals(x.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) ?? new(Environment.ProcessPath!, "", 0, 0);
            }
            foreach (bool ipv6 in new[] { false, true })
            foreach (bool udp in new[] { false, true })
            {
                var before = await Sample();
                int bytes = await Exchange(ipv6, udp);
                var after = await Sample();
                ulong received = after.Received - before.Received, sent = after.Sent - before.Sent;
                results[(udp ? "UDP" : "TCP") + (ipv6 ? "v6" : "v4")] = new { PayloadBytes = bytes, ObservedReceived = received, ObservedSent = sent, Covered = received >= (ulong)bytes && sent >= (ulong)bytes };
                if (received < (ulong)bytes || sent < (ulong)bytes) results["CoverageFailure"] = true;
            }
            await client.SendAsync(new("stop"));
            results["StopAcknowledged"] = true;
        }
        catch (Exception ex) { results["Error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "usage-selftest.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static async Task<int> Exchange(bool ipv6, bool udp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        var family = ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        byte[] payload = new byte[1024];
        if (udp)
        {
            using var receiver = new UdpClient(new IPEndPoint(address, 0));
            using var sender = new UdpClient(new IPEndPoint(address, 0));
            var destination = (IPEndPoint)receiver.Client.LocalEndPoint!;
            for (int i = 0; i < 128; i++)
            {
                await sender.SendAsync(payload, destination, token);
                var packet = await receiver.ReceiveAsync(token);
                if (packet.Buffer.Length != payload.Length) throw new IOException("Truncated fixture.");
            }
            return 128 * payload.Length;
        }
        using var listener = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(address, 0)); listener.Listen(1);
        using var outgoing = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        await outgoing.ConnectAsync(listener.LocalEndPoint!, token);
        using var incoming = await listener.AcceptAsync(token);
        int total = 0;
        for (int i = 0; i < 128; i++)
        {
            int sent = 0;
            while (sent < payload.Length) sent += await outgoing.SendAsync(payload.AsMemory(sent), SocketFlags.None, token);
            int received = 0;
            while (received < payload.Length)
            {
                int read = await incoming.ReceiveAsync(payload.AsMemory(received), SocketFlags.None, token);
                if (read == 0) throw new IOException("Fixture ended early.");
                received += read;
            }
            total += received;
        }
        return total;
    }
}
