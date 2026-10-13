using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;

namespace ZikWall.Setup;

public sealed partial class SetupWindow : Window
{
    private bool ready, busy, complete, removable;
    private InstalledProduct? installed;
    private InstallOperation operation;
    private InstallerEngine? engine;
    private string logPath = "";
    private readonly bool diagnostic = Environment.GetCommandLineArgs().Contains("--self-test");
    public SetupWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ZikWall.ico"));
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(1100, workArea.Width - 40), height = Math.Min(1060, workArea.Height - 40);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = false;
        AppWindow.Closing += (_, args) => { if (busy) { args.Cancel = true; RequestCancel(); } };
        Root.Loaded += async (_, _) =>
        {
            await InitializeAsync();
            if (diagnostic)
            {
                try { await ExerciseAsync(); }
                catch (Exception ex) { File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "setup-selftest.json"), System.Text.Json.JsonSerializer.Serialize(new { Error = ex.ToString() })); Environment.ExitCode = 1; }
                Close();
            }
        };
    }
    private async Task InitializeAsync()
    {
        try
        {
            VersionLabel.Text = $"Version {Package.Version} · Windows x64";
            installed = await Task.Run(() => { using var verified = Package.OpenVerified(Package.Path); return Package.Installed(); });
            string productCode = Package.ProductCode();
            operation = installed?.Code.Equals(productCode, StringComparison.OrdinalIgnoreCase) == true ? InstallOperation.Repair : InstallOperation.Install;
            InstallFolder.Text = !string.IsNullOrWhiteSpace(installed?.Directory) ? installed.Directory : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZikWall");
            InstallFolder.IsEnabled = installed is null;
            RemoveButton.Visibility = installed is null ? Visibility.Collapsed : Visibility.Visible;
            removable = installed is not null;
            if (installed is not null)
            {
                Heading.Text = "Keep ZikWall ready.";
                Introduction.Text = $"ZikWall {installed.Version} is installed on this PC. Your saved settings and history will be kept.";
                PrimaryButton.Content = operation == InstallOperation.Repair ? "Repair" : "Update";
                if (installed.Version > System.Version.Parse(Package.Version))
                {
                    Notice.Severity = InfoBarSeverity.Informational; Notice.Message = "A newer version is already installed. Use the latest setup to repair or update it."; Notice.IsOpen = true;
                    return;
                }
            }
            ready = true;
            UpdateButtons();
        }
        catch (Exception ex) { Notice.Message = ex.Message; Notice.IsOpen = true; }
    }
    private void OptionsChanged(object sender, RoutedEventArgs args) => UpdateButtons();
    private void OptionsChanged(object sender, TextChangedEventArgs args) => UpdateButtons();
    private void UpdateButtons()
    {
        if (PrimaryButton is null || busy || complete) return;
        PrimaryButton.IsEnabled = ready && AcceptLicense.IsChecked == true && !string.IsNullOrWhiteSpace(InstallFolder.Text);
    }
    private async void PrimaryClicked(object sender, RoutedEventArgs args)
    {
        if (complete) { Close(); return; }
        await RunAsync(operation);
    }
    private async void RemoveClicked(object sender, RoutedEventArgs args)
    {
        if (!removable || installed is null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Uninstall ZikWall?", Content = "ZikWall and its Start menu shortcut will be removed. Your saved settings and history will be kept.", PrimaryButtonText = "Uninstall", CloseButtonText = "Keep ZikWall", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunAsync(InstallOperation.Remove);
    }
    private async Task RunAsync(InstallOperation selected)
    {
        if (busy || diagnostic) return;
        string directory;
        try
        {
            directory = InstallerEngine.ValidateDirectory(InstallFolder.Text);
            foreach (string name in new[] { "ZikWall", "ZikWall.Broker" })
            {
                var processes = Process.GetProcessesByName(name);
                bool running = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (running) throw new InvalidOperationException("Close ZikWall using Exit in its tray menu, then try again.");
            }
        }
        catch (Exception ex) { Notice.Severity = InfoBarSeverity.Error; Notice.Message = ex.Message; Notice.IsOpen = true; return; }
        ShowProgress(selected);
        engine = new(new Progress<InstallProgress>(state => DispatcherQueue.TryEnqueue(() => ApplyProgress(state))));
        try
        {
            IntPtr owner = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var result = await Task.Run(() => engine.Run(selected, directory, installed, owner));
            ShowResult(selected, result);
        }
        catch (Exception ex) { ShowResult(selected, new(1603, ex.Message, "")); }
        finally { engine = null; }
    }
    private void ShowProgress(InstallOperation selected)
    {
        busy = true; Notice.IsOpen = false;
        Options.Visibility = ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        RemoveButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.IsIndeterminate = true;
        ProgressTitle.Text = selected switch { InstallOperation.Repair => "Repairing ZikWall", InstallOperation.Remove => "Removing ZikWall", _ => "Installing ZikWall" };
        Heading.Text = "ZikWall Setup";
        Introduction.Text = selected == InstallOperation.Remove ? "Removing the app while keeping your saved settings." : "Getting everything ready for your next launch.";
        ProgressMessage.Text = "Preparing installation…";
    }
    private void ApplyProgress(InstallProgress state)
    {
        if (!busy) return;
        Progress.IsIndeterminate = !state.Percent.HasValue;
        if (state.Percent.HasValue) Progress.Value = state.Percent.Value;
        ProgressMessage.Text = state.Action;
        CancelButton.IsEnabled = state.CanCancel;
    }
    private void ShowResult(InstallOperation selected, InstallResult result)
    {
        busy = false; complete = true; logPath = result.Log;
        Options.Visibility = ProgressPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        RemoveButton.Visibility = CancelButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = true; PrimaryButton.Content = "Finish";
        Heading.Text = "ZikWall";
        Introduction.Text = result.Succeeded ? "Setup has finished." : "You can close setup and try again when you are ready.";
        OpenLogButton.Visibility = File.Exists(logPath) ? Visibility.Visible : Visibility.Collapsed;
        ResultIcon.Glyph = result.Succeeded ? "\uE73E" : result.Cancelled ? "\uE711" : "\uE7BA";
        if (result.Succeeded)
        {
            ResultTitle.Text = selected == InstallOperation.Remove ? "ZikWall has been removed." : "ZikWall is ready.";
            ResultMessage.Text = selected == InstallOperation.Remove ? "Your saved settings and history have been kept." : "Open Start → All apps → ZikWall to get started. The shortcut is available to everyone on this PC.";
            if (result.NeedsRestart) ResultMessage.Text += " Restart Windows to finish applying the changes.";
        }
        else if (result.Cancelled)
        {
            ResultTitle.Text = "Setup was cancelled.";
            ResultMessage.Text = result.Error.Length > 0 ? result.Error : "Windows Installer has finished cancelling. You can run setup again whenever you are ready.";
        }
        else
        {
            ResultTitle.Text = "Setup could not finish.";
            ResultMessage.Text = result.Code == 1618 ? "Another installation is running. Wait for it to finish, then run setup again." : result.Error.Length > 0 ? result.Error : $"Windows Installer returned error {result.Code}. Open the installation log for details, then run setup again.";
        }
    }
    private void RequestCancel()
    {
        engine?.Cancel(); CancelButton.IsEnabled = false;
        ProgressMessage.Text = "Cancelling — waiting for Windows Installer…";
    }
    private void CancelClicked(object sender, RoutedEventArgs args) { if (busy) RequestCancel(); else Close(); }
    private async void ReadLicense(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "GNU General Public License v3", CloseButtonText = "Close", Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "LICENSE.txt")), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } } };
        await dialog.ShowAsync();
    }
    private void OpenLog(object sender, RoutedEventArgs args)
    {
        if (File.Exists(logPath)) Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { logPath } });
    }
}
