using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ZikWall.Setup;

public sealed partial class SetupWindow
{
    // Explicit, non-installing diagnostic: real XAML controls and MSI record callbacks.
    private async Task ExerciseAsync()
    {
        var checks = new List<string>();
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
        Check(ready, "Bundled MSI passes SHA-256 validation and installed-product discovery.");
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        Check(AppWindow.Size.Width <= workArea.Width && AppWindow.Size.Height <= workArea.Height, "Setup fits the monitor work area, including its footer actions.");
        Check(Guid.TryParse(Package.ProductCode(), out _), "Product code is read from the bundled MSI database.");
        string corrupt = System.IO.Path.Combine(AppContext.BaseDirectory, "corrupt-test.msi");
        try
        {
            File.WriteAllBytes(corrupt, [0, 1, 2, 3]);
            bool rejected = false;
            try { using var stream = Package.OpenVerified(corrupt); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "A corrupted installation package is rejected before installation.");
        }
        finally { File.Delete(corrupt); }
        AcceptLicense.IsChecked = false;
        Check(!PrimaryButton.IsEnabled, "Installation is disabled until the license is accepted.");
        AcceptLicense.IsChecked = true;
        Check(PrimaryButton.IsEnabled, "License acceptance enables installation.");
        foreach (string invalid in new[] { "relative", @"\\server\share\ZikWall", @"C:\", "C:\\Apps\\bad\" PROPERTY=1", "C:\\Apps\nBad" })
        {
            bool rejected = false;
            try { InstallerEngine.ValidateDirectory(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Unsafe installation folder rejected: " + invalid.Replace('\n', ' '));
        }
        Check(InstallerEngine.ValidateDirectory(@"C:\Program Files\ZikWall") == @"C:\Program Files\ZikWall", "A local folder with spaces is preserved.");
        var updates = new List<InstallProgress>();
        var tester = new InstallerEngine(new InlineProgress(updates));
        uint record = Native.MsiCreateRecord(4);
        try
        {
            void Fields(int first, int second, int third = 0, int fourth = 0)
            { Native.Check(Native.MsiRecordSetInteger(record, 1, first)); Native.Check(Native.MsiRecordSetInteger(record, 2, second)); Native.Check(Native.MsiRecordSetInteger(record, 3, third)); Native.Check(Native.MsiRecordSetInteger(record, 4, fourth)); }
            Fields(0, 100); tester.HandleMessage(IntPtr.Zero, 0x0A000000, record);
            Fields(2, 35); tester.HandleMessage(IntPtr.Zero, 0x0A000000, record);
            Check(updates.Last().Percent == 35, "Native MSI progress records advance to 35 percent.");
            Fields(0, 100); tester.HandleMessage(IntPtr.Zero, 0x0A000000, record);
            Fields(1, 5, 1); tester.HandleMessage(IntPtr.Zero, 0x0A000000, record);
            tester.HandleMessage(IntPtr.Zero, 0x09000000, record);
            Check(updates.Last().Percent == 5, "Action-data progress uses the MSI tick increment.");
            Fields(3, 100); tester.HandleMessage(IntPtr.Zero, 0x0A000000, record);
            Check(updates.Last().Percent == 2.5, "MSI progress accounts for additional work.");
            Fields(0, 100, 1); tester.HandleMessage(IntPtr.Zero, 0x0A000000, record);
            Fields(2, 20); tester.HandleMessage(IntPtr.Zero, 0x0A000000, record);
            Check(updates.Last().Percent == 80, "Reverse MSI progress decrements correctly.");
            Fields(2, 0); tester.HandleMessage(IntPtr.Zero, 0x0B000000, record);
            tester.Cancel();
            Fields(2, 1); Check(tester.HandleMessage(IntPtr.Zero, 0x0A000000, record) == 1, "Cancellation waits while Windows Installer disables it.");
            Fields(2, 1); tester.HandleMessage(IntPtr.Zero, 0x0B000000, record);
            Check(tester.HandleMessage(IntPtr.Zero, 0x0A000000, record) == 2, "Cancellation returns IDCANCEL to Windows Installer.");
            Check(tester.HandleMessage(IntPtr.Zero, 0x0A000000, record) == 1, "Rollback continues after the cancellation request.");
        }
        finally { Native.MsiCloseHandle(record); }
        Check(new InstallResult(3010, "", "").Succeeded && new InstallResult(3010, "", "").NeedsRestart, "Reboot-required success is recognized without restarting Windows.");
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            Root.RequestedTheme = theme;
            // RenderTargetBitmap excludes Mica. Supply its neutral fallback only in diagnostics.
            Root.Background = new SolidColorBrush(theme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
            complete = false; busy = false;
            Options.Visibility = Visibility.Visible; ProgressPanel.Visibility = ResultPanel.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible; CancelButton.IsEnabled = true;
            PrimaryButton.Content = operation == InstallOperation.Repair ? "Repair" : installed is null ? "Install" : "Update";
            UpdateButtons();
            await CaptureAsync($"setup-welcome-{theme.ToString().ToLowerInvariant()}.png");
            Check(PrimaryButton.ActualWidth >= 100 && Options.ActualWidth > 400, "Native setup content and actions have usable widths in " + theme + " theme.");
        }
        ShowProgress(InstallOperation.Install);
        ApplyProgress(new("Copying application files…", 62));
        Check(CancelButton.Visibility == Visibility.Visible && CancelButton.IsEnabled && PrimaryButton.Visibility == Visibility.Collapsed, "The progress screen exposes cancellation and prevents duplicate installation.");
        await CaptureAsync("setup-progress.png");
        ShowResult(InstallOperation.Install, new(0, "", ""));
        Check(ResultTitle.Text == "ZikWall is ready." && PrimaryButton.IsEnabled, "Successful installation shows the Start menu guidance and Finish action.");
        await CaptureAsync("setup-complete.png");
        ShowResult(InstallOperation.Install, new(1602, "", ""));
        Check(ResultTitle.Text == "Setup was cancelled.", "Cancellation has its own result screen.");
        ShowResult(InstallOperation.Install, new(1618, "", ""));
        Check(ResultMessage.Text.Contains("Another installation"), "A busy Windows Installer produces actionable feedback.");
        await CaptureAsync("setup-error.png");
        ShowResult(InstallOperation.Remove, new(0, "", ""));
        Check(ResultMessage.Text.Contains("kept"), "Removal explains settings retention.");
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "setup-selftest.json"), JsonSerializer.Serialize(new { Passed = true, PackageVersion = Package.Version, Checks = checks, InstallationPerformed = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private async Task CaptureAsync(string name)
    {
        Root.UpdateLayout(); await Task.Delay(200);
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(Root);
        var folder = await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }
    private sealed class InlineProgress(List<InstallProgress> updates) : IProgress<InstallProgress>
    { public void Report(InstallProgress value) => updates.Add(value); }
}
