using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed class FirewallClient : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    private NamedPipeClientStream? pipe;
    private readonly SemaphoreSlim gate = new(1);
    public bool Connected => pipe?.IsConnected == true;
    public async Task StartAsync(bool domainsOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (pipe is not null) throw new InvalidOperationException("A filtering session already exists.");
        string broker = Path.Combine(AppContext.BaseDirectory, "ZikWall.Broker.exe");
        if (!File.Exists(broker)) throw new FileNotFoundException("Build the ZikWall broker before enabling filtering.", broker);
        using var parent = Process.GetCurrentProcess();
        string id = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(broker) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory, Arguments = $"{id} {parent.Id} {parent.StartTime.ToUniversalTime().Ticks}" + (domainsOnly ? " domains" : ""), WindowStyle = ProcessWindowStyle.Hidden };
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the broker.");
        var connection = new NamedPipeClientStream(".", "ZikWall." + id, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await connection.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(connection.SafePipeHandle, out uint serverPid) || serverPid != process.Id) throw new IOException("The broker process identity did not match.");
            pipe = connection;
            var status = await SendAsync(new("status"));
            if (!status.Success) throw new IOException(status.Message);
        }
        catch { connection.Dispose(); pipe = null; throw; }
    }
    public async Task<BrokerResponse> SendAsync(BrokerRequest request)
    {
        // Local preflight failures must not disconnect a working broker and remove its filters.
        BrokerWire.ValidateSize(request);
        await gate.WaitAsync();
        try
        {
            var connection = pipe ?? throw new IOException("Filtering is not enabled.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await BrokerWire.WriteAsync(connection, request, timeout.Token);
            return await BrokerWire.ReadAsync<BrokerResponse>(connection, timeout.Token);
        }
        catch { Dispose(); throw; }
        finally { gate.Release(); }
    }
    public void Dispose() { pipe?.Dispose(); pipe = null; }
}
