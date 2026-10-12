using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using ZikWall.WinUI.Models;

using var identity = WindowsIdentity.GetCurrent();
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("NOT RUN: this isolated loopback WFP test requires an administrator terminal in a disposable Windows test environment.");
    return 2;
}
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
async Task<bool> ConnectAsync()
{
    using var socket = new TcpClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try { await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token); using var accepted = await listener.AcceptTcpClientAsync(timeout.Token); return true; }
    catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
}
if (!await ConnectAsync()) throw new Exception("Baseline loopback connection failed; no filters have been added.");
var rule = new FirewallRule(Guid.NewGuid(), "ZikWall isolated loopback test", Environment.ProcessPath!, "127.0.0.1/32", 6, port, 1, true, true, null);
using (var engine = new NativeEngine())
{
    engine.Apply([rule], DateTimeOffset.UtcNow);
    if (await ConnectAsync()) throw new Exception("The exact test-process/loopback-port block did not take effect.");
    engine.Apply([], DateTimeOffset.UtcNow);
    if (!await ConnectAsync()) throw new Exception("Removing the rule did not restore the loopback connection.");
    engine.Apply([rule], DateTimeOffset.UtcNow);
}
if (!await ConnectAsync()) throw new Exception("Closing the dynamic WFP session did not remove its filters.");
Console.WriteLine("PASS: scoped loopback block, atomic empty-policy replacement and session cleanup.");
return 0;
