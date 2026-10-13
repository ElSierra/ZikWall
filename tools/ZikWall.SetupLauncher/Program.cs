using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool diagnostic = args.SequenceEqual(new[] { "--self-test" });
        string? folder = null;
        try
        {
            if (args.Length > 0 && !diagnostic) throw new ArgumentException("Open setup.exe without arguments to install ZikWall.");
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new NotSupportedException("ZikWall requires 64-bit Windows 10 build 19041 or newer.");
            using var gate = new Mutex(true, @"Local\ZikWall.Setup." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out bool first);
            if (!first) throw new InvalidOperationException("ZikWall Setup is already open. Finish or close that window first.");
            var assembly = Assembly.GetExecutingAssembly();
            using var payload = assembly.GetManifestResourceStream("SetupPayload.zip") ?? throw new InvalidDataException("The setup payload is missing.");
            string expected = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == "PayloadSha256").Value!;
            if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Setup failed its integrity check. Download setup.exe again.");
            payload.Position = 0;
            folder = Path.Combine(Path.GetTempPath(), "ZikWall-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                    if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid setup payload path.");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            }
            var start = new ProcessStartInfo(Path.Combine(folder, "ZikWall.Setup.exe")) { UseShellExecute = false, WorkingDirectory = folder };
            if (diagnostic) start.ArgumentList.Add("--self-test");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not open ZikWall Setup.");
            process.WaitForExit();
            if (diagnostic)
            {
                string report = Path.Combine(folder, "setup-selftest.json");
                if (!File.Exists(report)) throw new InvalidOperationException("The setup diagnostic did not produce a report.");
                string output = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "setup-preview");
                Directory.CreateDirectory(output);
                File.Copy(report, Path.Combine(output, "setup-selftest.json"), true);
                foreach (string preview in Directory.EnumerateFiles(folder, "setup-*.png")) File.Copy(preview, Path.Combine(output, Path.GetFileName(preview)), true);
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            if (diagnostic)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "setup-launcher-error.txt"), ex.ToString());
            else MessageBox(IntPtr.Zero, ex.Message, "ZikWall Setup", 0x10);
            return 1;
        }
        finally
        {
            if (folder is not null)
            {
                // Only the unique directory created by this invocation is removed.
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string message, string title, uint type);
}
