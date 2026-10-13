using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ZikWall.WinUI.Services;

internal sealed class PassiveDnsCapture : IDisposable
{
    private readonly Packet callback;
    private readonly ObservedDnsCache cache;
    private bool started;
    private Process? controller;
    private readonly Task startup;
    private string failure = "";
    private long replies, rejected;
    public PassiveDnsCapture(ObservedDnsCache cache)
    {
        this.cache = cache; callback = Receive; startup = StartAsync();
    }
    private async Task StartAsync()
    {
        // NetEvent's documented configuration activates NDIS capture. Merely
        // enabling its ETW provider does not establish packet delivery.
        string session = "ZikWall.Dns." + Guid.NewGuid().ToString("N");
        string script = ControllerScript.Replace("SESSION_NAME", session, StringComparison.Ordinal);
        try
        {
            controller = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
            }) ?? throw new IOException("Windows did not start packet capture.");
            string? ready = await controller.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (ready != "READY") throw new IOException(ready ?? "Windows packet capture could not start.");
            uint error = zw_dns_start(session, callback);
            if (error != 0) throw new Win32Exception((int)error);
            started = true;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or TimeoutException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            failure = "Packet capture unavailable: " + ex.Message + " Windows cached names remain available.";
            StopController();
        }
    }
    // Stdin EOF on normal disposal OR broker death releases just this session.
    // Never remove another application's session to claim the singleton provider.
    // MultiLayer is required for receive coverage on the tested Wi-Fi adapter;
    // repeated layer observations refresh the same bounded address/name entries.
    private const string ControllerScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $name = 'SESSION_NAME'
        $owned = $false
        try {
            New-NetEventSession -Name $name -CaptureMode RealtimeLocal -MaxNumberOfBuffers 32 -TraceBufferSize 64 | Out-Null
            $owned = $true
            Add-NetEventPacketCaptureProvider -SessionName $name -Level 4 -MatchAnyKeyword 0x40000000000 -EtherType 0x800,0x86dd -IpProtocols 17 -MultiLayer $true -TruncationLength 65535 | Out-Null
            Start-NetEventSession -Name $name
            [Console]::Out.WriteLine('READY')
            [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
        } catch {
            [Console]::Out.WriteLine('ERROR: ' + $_.Exception.Message.Replace([Environment]::NewLine, ' '))
            [Console]::Out.Flush()
        } finally {
            if ($owned) {
                Stop-NetEventSession -Name $name -ErrorAction SilentlyContinue
                Remove-NetEventSession -Name $name -ErrorAction SilentlyContinue
            }
        }
        """;
    private void StopController()
    {
        if (controller is null) return;
        try { controller.StandardInput.Close(); controller.WaitForExit(15000); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        finally { controller.Dispose(); controller = null; }
    }
    private unsafe void Receive(IntPtr data, uint length, int media)
    {
        try
        {
            if (length > 65535) return;
            var frame = new ReadOnlySpan<byte>(data.ToPointer(), (int)length);
            var payload = media == 2 ? DnsPacketParser.WifiPayload(frame) : DnsPacketParser.Payload(frame, media == 1);
            if (payload.IsEmpty) return;
            var answers = DnsResponseParser.Parse(payload);
            if (answers.Count == 0) return;
            cache.Observe(answers); Interlocked.Increment(ref replies);
        }
        catch (Exception) { Interlocked.Increment(ref rejected); } // Never unwind through native ETW.
    }
    public string Status()
    {
        if (!startup.IsCompleted) return "Starting website-name capture; Windows cached names remain available.";
        if (!started) return failure;
        if (controller?.HasExited != false) return "Website-name capture stopped unexpectedly; Windows cached names remain available.";
        uint error = zw_dns_status(out ulong packets, out ulong unsupported, out uint lost);
        if (error != 0) return "Website-name capture unavailable: " + new Win32Exception((int)error).Message + ". Windows cached names remain available.";
        long count = Interlocked.Read(ref replies);
        return count > 0 ? $"Listening for website names · {count:N0} DNS reply observations · {lost} lost events · {unsupported + (ulong)Interlocked.Read(ref rejected)} unsupported packets."
            : packets > 0 ? "Listening for website names; no supported DNS reply observations yet."
            : "Capture started; packet delivery has not been observed yet. Windows cached names remain available.";
    }
    public void Dispose()
    {
        startup.GetAwaiter().GetResult();
        if (started) { started = false; zw_dns_stop(); GC.KeepAlive(callback); }
        StopController();
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Packet(IntPtr data, uint length, int media);
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern uint zw_dns_start(string session, Packet callback);
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint zw_dns_status(out ulong packets, out ulong rejected, out uint lost);
    [DllImport("ZikWall.Engine.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void zw_dns_stop();
}
