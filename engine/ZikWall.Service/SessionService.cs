using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ZikWall.WinUI.Services;

internal static class SessionService
{
    public static async Task RunAsync(CancellationToken stop)
    {
        using var job = ServiceSecurity.CreateJob();
        using var listener = ServiceSecurity.CreateListener();
        var children = new List<(Process Process, string Owner)>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(stop);
                Process? child = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var request = await ServiceProtocol.ReadAsync<ServiceSessionRequest>(listener, timeout.Token);
                    if (!ServiceProtocol.IsValid(request)) throw new InvalidDataException("Unsupported service request.");
                    using var parent = ServiceSecurity.Authenticate(listener);
                    string owner = ServiceSecurity.OwnerSid(parent);
                    for (int i = children.Count - 1; i >= 0; i--)
                    {
                        if (!children[i].Process.HasExited) continue;
                        children[i].Process.Dispose(); children.RemoveAt(i);
                    }
                    if (children.Count >= 32 || children.Count(x => x.Owner == owner) >= 6)
                        throw new InvalidDataException("Too many active ZikWall sessions. Close other ZikWall windows and retry.");
                    string id = Guid.NewGuid().ToString("N");
                    var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ZikWall.Broker.exe"))
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, WorkingDirectory = AppContext.BaseDirectory,
                    };
                    start.Environment["ZIKWALL_SERVICE_SESSION"] = "1";
                    start.ArgumentList.Add(id);
                    start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (request.Mode == "domains") start.ArgumentList.Add("domains");
                    child = Process.Start(start) ?? throw new IOException("Could not start the session helper.");
                    ServiceSecurity.Assign(job, child);
                    await ServiceProtocol.WriteAsync(listener, new ServiceSessionResponse(true, "Session started", id, child.Id), timeout.Token);
                    // DisconnectNamedPipe discards unread bytes. Wait for the UI
                    // to acknowledge consuming the response before reusing it.
                    if (!await ServiceProtocol.ReadAsync<bool>(listener, timeout.Token)) throw new IOException("Session response was not acknowledged.");
                    children.Add((child, owner)); child = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
                {
                    if (child is not null)
                    {
                        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                        catch (Exception cleanup) when (cleanup is Win32Exception or InvalidOperationException) { }
                        child.Dispose();
                    }
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await ServiceProtocol.WriteAsync(listener, new ServiceSessionResponse(false, "The service could not authorize or start this session. " + ex.Message), timeout.Token);
                        await ServiceProtocol.ReadAsync<bool>(listener, timeout.Token);
                    }
                    catch (Exception write) when (write is IOException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException) { }
                }
                finally
                {
                    try { listener.Disconnect(); }
                    catch (IOException) { } // A client may have already closed/broken its end.
                }
            }
        }
        finally
        {
            // Give helpers time to close ETW/NetEvent sessions and WFP handles.
            // The job remains a bounded fallback for a stuck helper.
            foreach (var (process, _) in children)
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
            }
            try { await Task.WhenAll(children.Select(x => x.Process.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { }
            ServiceSecurity.Terminate(job);
            foreach (var (process, _) in children)
            {
                try { process.WaitForExit(2000); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }
}
