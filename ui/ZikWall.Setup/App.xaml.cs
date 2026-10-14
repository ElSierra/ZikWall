using Microsoft.UI.Xaml;

namespace ZikWall.Setup;

public partial class App : Application
{
    private SetupWindow? window;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { ApplicationLifetime.CloseArgument }))
        {
            try { await Task.Run(() => ApplicationLifetime.CloseRunning(allowElevation: false)); }
            catch { Environment.ExitCode = 1; }
            Exit();
            return;
        }
        window = new SetupWindow();
        window.Activate();
    }
}
