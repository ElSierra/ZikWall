using Microsoft.UI.Xaml;

namespace ZikWall.WinUI;

public sealed partial class PrivacyView
{
    internal Task ExercisePreferencesAsync(string directory)
    {
        var first = new PrivacyView(directory);
        first.Show();
        first.DnsEndpoint.Text = "https://dns.example/dns-query";
        first.BootstrapAddress.Text = "192.0.2.1";
        first.DnsSettingsChanged(first, new RoutedEventArgs());
        var reopened = new PrivacyView(directory);
        try
        {
            reopened.Show();
            if (reopened.DnsEndpoint.Text != first.DnsEndpoint.Text || reopened.BootstrapAddress.Text != first.BootstrapAddress.Text)
                throw new InvalidOperationException("DNS provider fields reset when reopened.");
            if (new Services.AppearancePreferencesStore(directory).Load().Theme != 2)
                throw new InvalidOperationException("Editing DNS replaced the saved theme.");
        }
        finally { first.Stop(); reopened.Stop(); }
        return Task.CompletedTask;
    }
}
