using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ZikWall.Setup;

internal record InstalledProduct(string Code, Version Version, string Directory);
internal record InstallProgress(string Action, double? Percent, bool CanCancel = true);
internal record InstallResult(uint Code, string Error, string Log)
{
    public bool Succeeded => Code is 0 or 3010;
    public bool Cancelled => Code == 1602;
    public bool NeedsRestart => Code == 3010;
}
internal enum InstallOperation { Install, Repair, Remove }

internal static class Package
{
    internal const string UpgradeCode = "{A73F13E3-53D4-4D63-96AD-85460794860F}";
    internal static string Metadata(string name) => typeof(Package).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == name).Value!;
    internal static string Version => Metadata("PackageVersion");
    internal static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Payload", "setup.msi");
    internal static FileStream OpenVerified(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(Metadata("PackageSha256"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installation package failed its integrity check. Download setup again.");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    internal static string Cache()
    {
        // Retain the source cabinet for Windows Installer repair after setup.exe exits.
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall", "InstallerCache", Metadata("PackageSha256"));
        Directory.CreateDirectory(folder);
        string target = System.IO.Path.Combine(folder, "setup.msi");
        if (File.Exists(target))
        {
            try { using var existing = OpenVerified(target); return target; }
            catch (InvalidDataException) { /* Replace a damaged cache from the verified bundled source. */ }
        }
        using var source = OpenVerified(Path);
        string staging = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
            File.Move(staging, target, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return target;
    }
    internal static string ProductCode()
    {
        Native.Check(Native.MsiOpenDatabase(Path, IntPtr.Zero, out uint database));
        try
        {
            Native.Check(Native.MsiDatabaseOpenView(database, "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'", out uint view));
            try
            {
                Native.Check(Native.MsiViewExecute(view, 0));
                Native.Check(Native.MsiViewFetch(view, out uint record));
                try { return Native.Text(record, 1); }
                finally { Native.MsiCloseHandle(record); }
            }
            finally { Native.MsiCloseHandle(view); }
        }
        finally { Native.MsiCloseHandle(database); }
    }
    internal static InstalledProduct? Installed()
    {
        var found = new List<InstalledProduct>();
        for (uint i = 0; ; i++)
        {
            var code = new StringBuilder(39);
            uint result = Native.MsiEnumRelatedProducts(UpgradeCode, 0, i, code);
            if (result == 259) break;
            Native.Check(result);
            string version = Native.ProductInfo(code.ToString(), "VersionString");
            if (System.Version.TryParse(version, out var parsed)) found.Add(new(code.ToString(), parsed, Native.ProductInfo(code.ToString(), "InstallLocation")));
        }
        if (found.Count > 1) throw new InvalidOperationException("Multiple ZikWall installations were found. Remove the older copy in Windows Installed apps before continuing.");
        return found.SingleOrDefault();
    }
}

internal sealed class InstallerEngine
{
    private volatile bool cancelRequested;
    private bool cancelSent, canCancel = true, backwards, actionTicksEnabled;
    private long total, position, actionTicks;
    private string action = "Preparing installation…", error = "";
    private readonly IProgress<InstallProgress> progress;
    internal InstallerEngine(IProgress<InstallProgress> progress) => this.progress = progress;
    internal void Cancel() => cancelRequested = true;
    internal static string ValidateDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.IO.Path.IsPathFullyQualified(value) || value.StartsWith(@"\\") || value.Contains('"') || value.Any(char.IsControl))
            throw new ArgumentException("Choose a full path on a local drive, such as C:\\Program Files\\ZikWall.");
        string path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
        if (path.Equals(System.IO.Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase) || path.Length > 220)
            throw new ArgumentException("Choose an application folder inside a local drive (up to 220 characters).");
        return path;
    }
    internal InstallResult Run(InstallOperation operation, string directory, InstalledProduct? installed, IntPtr owner)
    {
        string log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall", "SetupLogs", $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
        string package = Package.Cache();
        using var lockedPackage = Package.OpenVerified(package);
        if (cancelRequested) return new(1602, "", log);
        progress.Report(new("Closing running ZikWall processes…", null));
        ApplicationLifetime.CloseRunning();
        if (cancelRequested) return new(1602, "", log);
        var callback = new Native.RecordHandler(HandleMessage);
        uint previousUi = Native.MsiSetInternalUI(2 | 0x200, ref owner); // NONE | UACONLY: native WinUI screens, standard Windows consent.
        bool handlerSet = false;
        try
        {
            Native.Check(Native.MsiEnableLog(0x3FFF, log, 0));
            Native.Check(Native.MsiSetExternalUIRecord(callback, 0x02003FFF, IntPtr.Zero, out _));
            handlerSet = true;
            string properties = "REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable";
            uint result;
            if (operation == InstallOperation.Remove)
                result = Native.MsiConfigureProductEx(installed?.Code ?? throw new InvalidOperationException("No installed product to remove."), 0, 2, properties);
            else
            {
                properties += $" ALLUSERS=1 INSTALLFOLDER=\"{ValidateDirectory(directory)}\"";
                if (operation == InstallOperation.Repair) properties += " REINSTALL=ALL REINSTALLMODE=vomus";
                result = Native.MsiInstallProduct(package, properties);
            }
            return new(result, error, log);
        }
        finally
        {
            if (handlerSet) Native.MsiSetExternalUIRecord(null, 0, IntPtr.Zero, out _);
            Native.MsiSetInternalUI(previousUi, ref owner);
            Native.MsiEnableLog(0, null, 0);
            GC.KeepAlive(callback);
        }
    }
    internal int HandleMessage(IntPtr context, uint messageType, uint record)
    {
        // Never allow a managed exception to cross the Windows Installer callback boundary.
        try
        {
            uint type = messageType & 0xFF000000;
            int Field(uint index) => Native.MsiRecordGetInteger(record, index);
            if (type == 0x01000000) error = Native.Format(record);
            if (type is 0x05000000 or 0x19000000)
            {
                error = "Another application is still using ZikWall files. Close it, then try setup again.";
                cancelSent = true;
                return 2;
            }
            if (type == 0x08000000) { action = Native.Text(record, 2); actionTicksEnabled = false; }
            if (type == 0x0B000000 && Field(1) == 2) canCancel = Field(2) != 0;
            if (type == 0x0A000000)
            {
                switch (Field(1))
                {
                    case 0: total = Math.Max(0, Field(2)); backwards = Field(3) != 0; position = backwards ? total : 0; break;
                    case 1: actionTicks = Math.Max(0, Field(2)); actionTicksEnabled = Field(3) != 0; break;
                    case 2: position += backwards ? -Field(2) : Field(2); break;
                    case 3: total += Math.Max(0, Field(2)); break;
                }
            }
            if (type == 0x09000000 && actionTicksEnabled) position += backwards ? -actionTicks : actionTicks;
            if (type is 0x08000000 or 0x09000000 or 0x0A000000 or 0x0B000000)
                progress.Report(new(cancelSent ? "Cancelling — waiting for Windows Installer…" : action, total > 0 ? Math.Clamp(100d * position / total, 0, 100) : null, canCancel && !cancelSent));
            if (cancelRequested && !cancelSent && canCancel && type is 0x08000000 or 0x09000000 or 0x0A000000)
            { cancelSent = true; return 2; }
            return 1;
        }
        catch (Exception ex) { error = ex.Message; return 2; }
    }
}

internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int RecordHandler(IntPtr context, uint messageType, uint record);
    internal static void Check(uint code) { if (code != 0) throw new System.ComponentModel.Win32Exception((int)code); }
    internal static string Text(uint record, uint field)
    {
        uint size = 0; MsiRecordGetString(record, field, null, ref size);
        var text = new StringBuilder((int)size + 1); size++;
        Check(MsiRecordGetString(record, field, text, ref size)); return text.ToString();
    }
    internal static string Format(uint record)
    {
        uint size = 0; MsiFormatRecord(0, record, null, ref size);
        var text = new StringBuilder((int)size + 1); size++;
        Check(MsiFormatRecord(0, record, text, ref size)); return text.ToString();
    }
    internal static string ProductInfo(string code, string property)
    {
        uint size = 0; uint result = MsiGetProductInfo(code, property, null, ref size);
        if (result is not (0 or 234)) return "";
        var value = new StringBuilder((int)size + 1); size++;
        return MsiGetProductInfo(code, property, value, ref size) == 0 ? value.ToString() : "";
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiInstallProductW")] internal static extern uint MsiInstallProduct(string package, string properties);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiConfigureProductExW")] internal static extern uint MsiConfigureProductEx(string product, int level, int state, string properties);
    [DllImport("msi.dll")] internal static extern uint MsiSetInternalUI(uint level, ref IntPtr window);
    [DllImport("msi.dll")] internal static extern uint MsiSetExternalUIRecord(RecordHandler? handler, uint filter, IntPtr context, out IntPtr previous);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnableLogW")] internal static extern uint MsiEnableLog(uint mode, string? path, uint attributes);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")] internal static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")] internal static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")] internal static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] internal static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll")] internal static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll")] internal static extern int MsiRecordGetInteger(uint record, uint field);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")] internal static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiFormatRecordW")] internal static extern uint MsiFormatRecord(uint install, uint record, StringBuilder? value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")] internal static extern uint MsiEnumRelatedProducts(string upgrade, uint reserved, uint index, StringBuilder product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")] internal static extern uint MsiGetProductInfo(string product, string property, StringBuilder? value, ref uint length);
    [DllImport("msi.dll")] internal static extern uint MsiCreateRecord(uint count);
    [DllImport("msi.dll")] internal static extern uint MsiRecordSetInteger(uint record, uint field, int value);
}
