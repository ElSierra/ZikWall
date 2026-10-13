using Microsoft.UI.Xaml;

namespace ZikWall.WinUI;

public partial class App : Application
{
    private Window? window;
    private Mutex? instance;
    private EventWaitHandle? wake;
    private RegisteredWaitHandle? wakeRegistration;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { "--test-domain-benchmark" }))
        {
            await Services.DomainCaptureBenchmark.RunAsync(); Exit(); return;
        }
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { "--test-throughput" }))
        {
            await Services.ThroughputSelfTest.RunAsync(); Exit(); return;
        }
        // Explicit diagnostic mode runs the real unelevated client and UAC broker.
        // Its rule scope is fixed in code; no policy or output path is accepted.
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { "--test-broker" }))
        {
            await Services.BrokerSelfTest.RunAsync();
            Exit();
            return;
        }
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool diagnostic = arguments.Any(x => x.StartsWith("--test-", StringComparison.Ordinal) || x == "--benchmark");
        if (arguments.Contains("--enable-startup")) Services.StartupSettings.Set(true);
        if (!diagnostic)
        {
            string suffix = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            instance = new Mutex(true, @"Local\ZikWall.Instance." + suffix, out bool first);
            wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ZikWall.Show." + suffix);
            if (!first) { if (!arguments.Contains("--tray")) wake.Set(); Exit(); return; }
        }
        var main = new MainWindow(diagnostic && !arguments.Contains("--test-lifecycle"));
        window = main;
        if (arguments.SequenceEqual(new[] { "--test-readme-screenshots" }))
        {
            window.Activate();
            try { await Task.Delay(500); await main.CaptureReadmeScreenshotsAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "readme-screenshots.txt"), ex.ToString()); Environment.ExitCode = 1; }
            window.Close(); return;
        }
        if (arguments.SequenceEqual(new[] { "--test-notification" }) || arguments.SequenceEqual(new[] { "--test-notification-icon" }))
        {
            var report = new Dictionary<string, object>();
            try
            {
                var sample = new Models.ConnectionEntry("TCPv4", "192.0.2.1", 50000, "1.1.1.1", 443, Environment.ProcessId,
                    "Established", "ZikWall", ProcessPath: Environment.ProcessPath!, CountryCode: "AU", CountryName: "Australia");
                var message = Models.ConnectionNotification.From(new([sample], 1, Models.ConnectionNotificationType.NewApps));
                string iconDirectory = Path.Combine(AppContext.BaseDirectory, "notification-icon-test");
                string xml = await Presentation.DesktopNotifications.CreateToastXmlAsync(message, iconDirectory);
                report["Xml"] = xml;
                string iconPath = new Uri(System.Xml.Linq.XDocument.Parse(xml).Descendants("image").Single().Attribute("src")!.Value).LocalPath;
                if (Path.GetDirectoryName(iconPath) != iconDirectory) throw new InvalidOperationException("Notification did not use the executable's icon.");
                using (var stream = await (await Windows.Storage.StorageFile.GetFileFromPathAsync(iconPath)).OpenReadAsync())
                {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                    if (decoder.PixelWidth != 192 || decoder.PixelHeight != 192) throw new InvalidOperationException("Notification icon is not high resolution.");
                    report["IconSize"] = $"{decoder.PixelWidth}x{decoder.PixelHeight}";
                }
                if (arguments[0] == "--test-notification")
                {
                    using var notifications = new Presentation.DesktopNotifications();
                    notifications.Enable();
                    await notifications.ShowAsync(message with { Title = "Notification test: " + message.Title });
                    report["Submitted"] = true;
                }
            }
            catch (Exception ex) { report["Error"] = ex.ToString(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "notification-selftest.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            main.Close(); return;
        }
        if (arguments.Contains("--test-log-analysis"))
        {
            window.Activate();
            try { await Task.Delay(500); await main.ExerciseLogAnalysisAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "log-analysis-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.Contains("--test-passive-dns"))
        {
            await Services.PassiveDnsSelfTest.RunAsync(); main.Close(); return;
        }
        if (arguments.Contains("--test-domain-cache"))
        {
            await Services.DomainCacheSelfTest.RunAsync(); main.Close(); return;
        }
        if (arguments.Contains("--test-usage"))
        {
            await Services.UsageSelfTest.RunAsync(); main.Close(); return;
        }
        if (wake is not null) wakeRegistration = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => main.DispatcherQueue.TryEnqueue(main.ShowFromTray), null, -1, false);
        window.Activate();
        if (arguments.Contains("--test-activity-overview"))
        {
            try { await Task.Delay(500); await main.ExerciseActivityOverviewAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "activity-overview-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.SequenceEqual(new[] { "--test-policy-layout" }))
        {
            try { await Task.Delay(500); await main.ExercisePolicyLayoutAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "policy-layout-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.SequenceEqual(new[] { "--test-preferences" }))
        {
            try { await main.ExercisePreferencesAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preferences-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.SequenceEqual(new[] { "--test-system-apps" }))
        {
            try { await Task.Delay(500); await main.ExerciseSystemAppsAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "system-apps-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.SequenceEqual(new[] { "--test-usage-view" }) || arguments.SequenceEqual(new[] { "--test-usage-view-benchmark" }))
        {
            try { await Task.Delay(500); await main.ExerciseUsageViewAsync(arguments[0].EndsWith("-benchmark")); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "usage-view-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.SequenceEqual(new[] { "--test-monitor-options" }))
        {
            try { await Task.Delay(500); await main.ExerciseMonitorOptionsAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "monitor-options-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.SequenceEqual(new[] { "--test-rules" }))
        {
            try { await Task.Delay(1000); await main.ExerciseRulesAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rules-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (arguments.Contains("--tray")) main.HideToTray();
        if (arguments.Contains("--test-lifecycle"))
        {
            try { await main.ExerciseLifecycleAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "lifecycle-selftest.txt"), ex.ToString()); }
            main.ExitFromTray(); return;
        }
        if (arguments.Contains("--test-map"))
        {
            await Task.Delay(1000); await main.ShowMapFixtureAsync();
            await Presentation.MonitorSelfTest.RunAsync(window, true); window.Close(); return;
        }
        window.Closed += (_, _) => { wakeRegistration?.Unregister(null); wake?.Dispose(); instance?.Dispose(); };
        if (arguments.SequenceEqual(new[] { "--test-context-menu" }))
        {
            try { await Task.Delay(1000); await main.ExerciseContextMenuAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "context-menu-selftest.txt"), ex.ToString()); }
            window.Close(); return;
        }
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { "--test-monitor-churn" }))
        {
            await ((MainWindow)window).ExerciseMonitorAsync();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "monitor-churn.txt"), "PASS: 500 native row churn and notification-layout iterations.");
            window.Close();
        }
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { "--test-monitor" }))
        {
            await Presentation.MonitorSelfTest.RunAsync(window);
            window.Close();
        }
    }
}
