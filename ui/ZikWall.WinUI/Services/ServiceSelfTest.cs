using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace ZikWall.WinUI.Services;

internal static class ServiceSelfTest
{
    internal static async Task RunAsync()
    {
        var checks = new List<string>();
        string? failure = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Run this diagnostic from an unelevated app.");
            if (!ServiceSessionClient.IsInstalled) throw new InvalidOperationException("Install this build before testing the service.");
            for (int launch = 0; launch < 2; launch++)
            {
                int[] pids;
                using (var domains = new FirewallClient())
                using (var usage = new FirewallClient())
                {
                    await domains.StartAsync(domainsOnly: true);
                    await usage.StartAsync();
                    if (!domains.UsesService || !usage.UsesService) throw new InvalidOperationException("A session used UAC fallback.");
                    if (!(await domains.SendAsync(new("status"))).Success || !(await usage.SendAsync(new("usage-start"))).Success)
                        throw new InvalidOperationException("A service session failed.");
                    if ((await domains.SendAsync(new("apply", []))).Success)
                        throw new InvalidOperationException("Observation session accepted a firewall command.");
                    pids = [domains.BrokerProcessId, usage.BrokerProcessId];
                    checks.Add($"Launch {launch + 1}: both sessions used the service; usage started; domain session rejected filtering.");
                }
                foreach (int pid in pids)
                {
                    bool alive = true;
                    for (int attempt = 0; attempt < 120 && alive; attempt++)
                    {
                        var processes = Process.GetProcessesByName("ZikWall.Broker");
                        try { alive = processes.Any(process => process.Id == pid); }
                        finally { foreach (var process in processes) process.Dispose(); }
                        if (alive) await Task.Delay(250);
                    }
                    if (alive) throw new InvalidOperationException("A helper survived app disconnect.");
                }
                checks.Add($"Launch {launch + 1}: both helpers exited after disconnect.");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "service-self-test.json"), JsonSerializer.Serialize(new
        { Success = failure is null, Checks = checks, Failure = failure }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
    }
}
