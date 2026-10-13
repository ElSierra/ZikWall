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
