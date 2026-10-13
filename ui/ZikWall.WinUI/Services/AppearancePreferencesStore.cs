using System.Text.Json;

namespace ZikWall.WinUI.Services;

public sealed record AppearancePreferences(int Theme = 0,
    string DnsEndpoint = "https://cloudflare-dns.com/dns-query", string BootstrapAddress = "1.1.1.1");

// Stable per-user location; no executable path or application version in the key.
// Each panel updates the latest document so a theme edit cannot discard DNS settings.
public sealed class AppearancePreferencesStore(string directory)
{
    private readonly string path = Path.Combine(directory, "appearance-preferences.json");
    private static readonly object gate = new();
    public AppearancePreferences Load()
    {
        lock (gate)
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Preferences are too large.");
            var value = JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(path)) ?? new();
            return value with { Theme = value.Theme is >= 0 and <= 2 ? value.Theme : 0,
                DnsEndpoint = value.DnsEndpoint ?? "https://cloudflare-dns.com/dns-query", BootstrapAddress = value.BootstrapAddress ?? "1.1.1.1" };
        }
    }
    public void Update(Func<AppearancePreferences, AppearancePreferences> update)
    {
        lock (gate)
        {
            var next = update(Load());
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(path + ".tmp", path, true);
        }
    }
}
