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
        var observed = new ObservedDnsCache();
        PassiveDnsCapture? capture = null;
        string captureError = "";
        try { capture = new(observed); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { captureError = "Packet capture unavailable: " + ex.Message + " Windows cached names remain available."; }
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
                    if (request.Command == "status") response = new(true, capture?.Status() ?? captureError);
                    else if (request.Command == "domains")
                    {
                        var wanted = Validate(request);
                        long now = Environment.TickCount64;
                        var answers = observed.Read(wanted);
                        string status = capture?.Status() ?? captureError;
                        if (now >= nextRead)
                        {
                            nextRead = now + 59000;
                            try { answers.AddRange(cache.Read(wanted).Take(8192 - answers.Count)); }
                            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
                            { status += " Windows cache unavailable: " + ex.Message; }
                        }
                        response = new(true, status, Domains: answers);
                    }
                    else throw new InvalidDataException("Only status and domain-cache reads are allowed in this session.");
                }
                catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or NotSupportedException or System.Runtime.InteropServices.ExternalException)
                { response = new(false, ex.Message); }
                await BrokerWire.WriteAsync(pipe, response, timeout.Token);
            }
        }
        finally { parent.Exited -= Exited; capture?.Dispose(); }
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
