using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

internal static class DomainCacheSession
{
    public static async Task RunAsync(NamedPipeServerStream pipe, Process parent)
    {
        var cache = new WindowsDnsCache();
        long nextRead = 0;
        using var lifetime = new CancellationTokenSource();
        parent.EnableRaisingEvents = true;
        void Exited(object? sender, EventArgs args) => lifetime.Cancel();
        parent.Exited += Exited;
        try
        {
            while (!parent.HasExited)
            {
                // No polling timer. A paused/disconnected UI releases the helper.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(150));
                var request = await BrokerWire.ReadAsync<BrokerRequest>(pipe, timeout.Token);
                BrokerResponse response;
                try
                {
                    if (request.Command == "status") response = new(true, "Read-only DNS cache session ready");
                    else if (request.Command == "domains")
                    {
                        var wanted = Validate(request);
                        long now = Environment.TickCount64;
                        if (now < nextRead) response = new(true, "DNS cache refresh throttled", Domains: []);
                        else
                        {
                            nextRead = now + 59000; // Defense in depth; UI cadence is >= 60 seconds.
                            response = new(true, "Windows DNS cache hints", Domains: cache.Read(wanted).Take(8192).ToList());
                        }
                    }
                    else throw new InvalidDataException("Only status and domain-cache reads are allowed in this session.");
                }
                catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or NotSupportedException or System.Runtime.InteropServices.ExternalException)
                { response = new(false, ex.Message); }
                await BrokerWire.WriteAsync(pipe, response, timeout.Token);
            }
        }
        finally { parent.Exited -= Exited; }
    }

    internal static HashSet<string> Validate(BrokerRequest request)
    {
        if (request.Rules is not null || request.RemoteAddresses is not { Count: <= CachedDomainHints.MaxAddresses } addresses)
            throw new InvalidDataException("Expected at most 2048 remote IP addresses.");
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string address in addresses)
        {
            if (address is null || address.Length > 64 || !IPAddress.TryParse(address, out var ip))
                throw new InvalidDataException("Domain cache requests accept IP addresses only.");
            wanted.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString());
        }
        return wanted;
    }
}
