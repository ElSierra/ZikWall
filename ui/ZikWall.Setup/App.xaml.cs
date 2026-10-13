using Microsoft.UI.Xaml;

namespace ZikWall.Setup;

public partial class App : Application
{
    private SetupWindow? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new SetupWindow();
        window.Activate();
    }
}
