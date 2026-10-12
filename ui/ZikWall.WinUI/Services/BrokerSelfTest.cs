using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Opt-in developer diagnostic: exercise the production privilege boundary without
// applying saved policies. Only this executable and an ephemeral loopback port match.
internal static class BrokerSelfTest
{
    public static async Task RunAsync()
    {
        var checks = new List<string>();
        string? failure = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Launch ZikWall unelevated to test the cross-integrity boundary.");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            async Task<bool> ConnectAsync()
            {
                using var socket = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
                    return true;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
            }
            void Check(bool result, string name)
            {
                if (!result) throw new InvalidOperationException(name);
                checks.Add(name);
            }
            async Task ExpectAsync(bool connected, string name)
            {
                Check(await ConnectAsync() == connected, name);
            }
            await ExpectAsync(true, "Baseline loopback connection");
            using var client = new FirewallClient();
            await client.StartAsync();
            Check(client.Connected, "Unelevated client to elevated broker handshake");
            var rule = new FirewallRule(Guid.NewGuid(), "ZikWall isolated broker test", Environment.ProcessPath!,
                "127.0.0.1/32", 6, port, 1, true, true, null);
            var applied = await client.SendAsync(new("apply", [rule]));
            Check(applied.Success && applied.ActiveRules == 1, "Broker accepted one scoped rule");
            await ExpectAsync(false, "Scoped rule blocks loopback");
            var invalid = await client.SendAsync(new("apply", [rule with { Protocol = -1 }]));
            Check(!invalid.Success && invalid.ActiveRules == 1, "Broker rejects invalid replacement");
            await ExpectAsync(false, "Rejected replacement preserves active block");
            Check((await client.SendAsync(new("apply", []))).Success, "Broker accepts empty policy");
            await ExpectAsync(true, "Empty policy restores loopback");
            Check((await client.SendAsync(new("apply", [rule with { Expires = DateTimeOffset.UtcNow.AddSeconds(4) }]))).Success,
                "Broker accepts expiring rule");
            await ExpectAsync(false, "Expiring rule initially blocks");
            await Task.Delay(TimeSpan.FromSeconds(5));
            // No requests during this wait: expiry must happen inside the broker.
            await ExpectAsync(true, "Broker independently removes expired rule");
            Check((await client.SendAsync(new("status"))).ActiveRules == 0, "Broker reports expiry");
            Check((await client.SendAsync(new("apply", [rule]))).Success, "Reapply before disconnect");
            await ExpectAsync(false, "Reapplied rule blocks");
            client.Dispose();
            // WFP cleanup is asynchronous after pipe close; allow a bounded grace period.
            bool restored = false;
            for (int attempt = 0; attempt < 20 && !restored; attempt++)
            {
                await Task.Delay(250);
                restored = await ConnectAsync();
            }
            Check(restored, "Pipe closure removes session filters");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        string output = Path.Combine(AppContext.BaseDirectory, "broker-self-test.json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.UtcNow, Success = failure is null, Checks = checks, Failure = failure
        }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
    }
}
