using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed partial class PrivacyView : UserControl
{
    private readonly PrivacyStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private readonly AppearancePreferencesStore preferencesStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private readonly bool diagnostic = Environment.GetCommandLineArgs().Skip(1).Any(x => x.StartsWith("--test-", StringComparison.Ordinal) || x == "--benchmark");
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private PrivacySettings settings = PrivacySettings.Empty();
    private DnsProxy? proxy;
    private bool loaded, refreshing, downloading, closed;
    private DateTimeOffset nextUpdate = DateTimeOffset.MinValue;
    public event Action<IReadOnlyList<string>>? CopyNetworks;
    public bool DnsRunning => proxy is not null;
    public PrivacyView() : this(null) { }
    public Visibility NoSubscriptions(object? items) => items is System.Collections.ICollection { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
    internal PrivacyView(string? preferencesDirectory)
    {
        if (preferencesDirectory is not null) { store = new(preferencesDirectory); preferencesStore = new(preferencesDirectory); diagnostic = false; }
        InitializeComponent();
        timer.Tick += async (_, _) => { UpdateCounts(); if (settings.AutomaticUpdates && DateTimeOffset.UtcNow >= nextUpdate && !downloading) await RefreshListsAsync(); };
    }
    public void Show()
    {
        if (loaded) return; loaded = true;
        try { settings = store.Load(); } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Status(ex.Message, true); }
        if (!diagnostic)
            try { var saved = preferencesStore.Load(); DnsEndpoint.Text = saved.DnsEndpoint; BootstrapAddress.Text = saved.BootstrapAddress; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Status($"Could not load DNS preferences: {ex.Message}", true); }
        Refresh(); timer.Start();
    }
    public void Stop() { SaveDnsPreferences(); closed = true; timer.Stop(); proxy?.Dispose(); proxy = null; }
    private void DnsSettingsChanged(object sender, RoutedEventArgs args) => SaveDnsPreferences();
    private void SaveDnsPreferences()
    {
        if (!loaded || diagnostic) return;
        try { preferencesStore.Update(value => value with { DnsEndpoint = DnsEndpoint.Text.Trim(), BootstrapAddress = BootstrapAddress.Text.Trim() }); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Status($"DNS choices could not be saved: {ex.Message}", true); }
    }
    private void Refresh()
    {
        refreshing = true; Subscriptions.ItemsSource = settings.Subscriptions.ToList(); AutomaticUpdates.IsChecked = settings.AutomaticUpdates; Exceptions.Text = string.Join(Environment.NewLine, settings.AllowedDomains); refreshing = false;
        proxy?.SetLists(settings.Subscriptions.SelectMany(x => x.Data.Domains), settings.AllowedDomains);
    }
    private void Save(PrivacySettings next) { store.Save(next); settings = next; Refresh(); }
    private void Status(string message, bool error = false) { PrivacyStatus.Title = error ? "Action failed" : proxy is null ? "DNS proxy is off" : "Local DNS proxy is running"; PrivacyStatus.Message = message; PrivacyStatus.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational; }
    private async void DnsClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            if (proxy is not null)
            {
                var confirm = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Stop the DNS proxy?", Content = "If your adapter uses 127.0.0.1 for DNS, restore its normal DNS settings before stopping the proxy.", PrimaryButtonText = "Stop proxy", CloseButtonText = "Keep running" };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                proxy.Dispose(); proxy = null; Status("Proxy stopped. Your adapter must use another DNS server for name resolution.");
            }
            else
            {
                if (!Uri.TryCreate(DnsEndpoint.Text.Trim(), UriKind.Absolute, out var endpoint) || !IPAddress.TryParse(BootstrapAddress.Text.Trim(), out var bootstrap)) throw new ArgumentException("Enter an HTTPS endpoint and its bootstrap IP address.");
                SaveDnsPreferences();
                proxy = new DnsProxy(endpoint, bootstrap); Refresh(); Status("Listening on 127.0.0.1:53. Adapter DNS settings have not been changed.");
            }
            DnsEndpoint.IsEnabled = BootstrapAddress.IsEnabled = proxy is null; DnsButton.Content = proxy is null ? "Start local DNS proxy" : "Stop local DNS proxy";
        }
        catch (Exception ex) when (ex is ArgumentException or SocketException) { Status(ex.Message, true); }
    }
    private void UpdateCounts() { if (proxy is { } active) { var count = active.Counts; DnsCounts.Text = $"{count.Queries} queries · {count.Blocked} blocked by domain · {count.Errors} errors"; } }
    private async void AddClicked(object sender, RoutedEventArgs args)
    {
        if (downloading) return;
        var name = new TextBox { Header = "Name", MaxLength = 128 }; var url = new TextBox { Header = "HTTPS URL" }; var hash = new TextBox { Header = "Optional SHA-256 pin (updates must match)", MaxLength = 64 };
        var form = new StackPanel { Spacing = 12, MinWidth = 400 }; form.Children.Add(name); form.Children.Add(url); form.Children.Add(hash);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Subscribe to a blocklist", Content = form, PrimaryButtonText = "Download and add", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        downloading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidDataException("Give this subscription a name.");
            var subscription = await BlocklistService.DownloadAsync(new(Guid.NewGuid(), name.Text.Trim(), url.Text.Trim(), hash.Text.Trim(), null, new([], [], 0)), CancellationToken.None);
            if (closed) return;
            Save(settings with { Subscriptions = [.. settings.Subscriptions, subscription] });
            Status($"Added {subscription.Data.Domains.Count} domains and {subscription.Data.Networks.Count} IP networks; {subscription.Data.UnsupportedLines} unsupported lines skipped.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { Status(ex.Message, true); }
        finally { downloading = false; }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs args) => await RefreshListsAsync();
    private async Task RefreshListsAsync()
    {
        if (downloading || closed) return; downloading = true; nextUpdate = DateTimeOffset.UtcNow.AddHours(24);
        int updated = 0; var errors = new List<string>();
        try
        {
            foreach (var item in settings.Subscriptions.ToList())
            {
                try
                {
                    var next = await BlocklistService.DownloadAsync(item, CancellationToken.None);
                    if (closed) return;
                    if (settings.Subscriptions.All(x => x.Id != item.Id)) continue;
                    Save(settings with { Subscriptions = settings.Subscriptions.Select(x => x.Id == item.Id ? next : x).ToList() }); updated++;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { errors.Add($"{item.Name}: {ex.Message}"); }
            }
            Status($"Refreshed {updated} lists. Previous data retained for failures. " + string.Join("; ", errors), errors.Count > 0);
        }
        finally { downloading = false; }
    }
    private void RemoveClicked(object sender, RoutedEventArgs args)
    {
        if (Subscriptions.SelectedItem is not BlocklistSubscription item) return;
        try { Save(settings with { Subscriptions = settings.Subscriptions.Where(x => x.Id != item.Id).ToList() }); Status("Subscription removed. Copied IP policy rules are separate and remain until you remove them."); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Status(ex.Message, true); }
    }
    private void AutomaticChanged(object sender, RoutedEventArgs args)
    {
        if (!loaded || refreshing) return;
        try { Save(settings with { AutomaticUpdates = AutomaticUpdates.IsChecked == true }); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Status(ex.Message, true); }
    }
    private void ExceptionsClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var domains = Exceptions.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => BlocklistParser.Domain(x) ?? throw new InvalidDataException($"Invalid domain: {x}")).Distinct().ToList();
            Save(settings with { AllowedDomains = domains }); Status("Domain exceptions saved.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Status(ex.Message, true); }
    }
    private void CopyIpsClicked(object sender, RoutedEventArgs args)
    {
        var networks = settings.Subscriptions.SelectMany(x => x.Data.Networks).Distinct().ToList();
        if (networks.Count == 0) { Status("These subscriptions contain no IP networks. Domain entries are used by the DNS proxy."); return; }
        CopyNetworks?.Invoke(networks);
    }
}
