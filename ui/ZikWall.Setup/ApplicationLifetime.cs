using System.ComponentModel;
using System.Diagnostics;

namespace ZikWall.Setup;

internal static class ApplicationLifetime
{
    internal const string CloseArgument = "--close-running";

    internal static void CloseRunning(bool allowElevation = true)
    {
        try
        {
            // Close the UI first so it cannot recreate its broker while setup stops it.
            foreach (string name in new[] { "ZikWall", "ZikWall.Broker" })
            {
                var processes = Process.GetProcessesByName(name);
                var interactive = new List<Process>();
                foreach (var process in processes)
                {
                    // MSI StopServices owns SYSTEM/session-zero helpers. Asking
                    // this unelevated wizard to kill them would add another UAC.
                    if (process.SessionId == 0) process.Dispose();
                    else interactive.Add(process);
                }
                StopProcesses(interactive.ToArray());
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 && allowElevation)
        {
            // Setup normally stays unelevated, including the post-install app launch.
            // Only this short-lived helper needs elevation for an elevated app/broker.
            using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", ArgumentList = { CloseArgument },
                WorkingDirectory = AppContext.BaseDirectory,
            }) ?? throw new InvalidOperationException("Could not close the running ZikWall processes.");
            helper.WaitForExit();
            if (helper.ExitCode != 0)
                throw new InvalidOperationException("Windows could not stop ZikWall. Close it from its tray menu and try setup again.");
        }
    }

    internal static void StopProcesses(Process[] processes)
    {
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    process.CloseMainWindow();
                    // The app may minimize to its tray, and the broker has no window.
                    if (!process.WaitForExit(2000)) process.Kill();
                    if (!process.WaitForExit(5000))
                        throw new InvalidOperationException("ZikWall did not stop in time. Try setup again.");
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
