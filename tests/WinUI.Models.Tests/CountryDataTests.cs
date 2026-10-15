using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class CountryDataTests
{
    public static void Run()
    {
        string directory = Environment.GetEnvironmentVariable("ZIKWALL_TEST_COUNTRIES") ?? Path.GetFullPath("ui/ZikWall.WinUI/Assets/Countries");
        var row = new ConnectionEntry("UDPv4", "192.168.1.2", 51000, "8.8.8.8", 443, 1, "Established");
        using var first = new MonitorEnrichment(directory);
        using var second = new MonitorEnrichment(directory);
        foreach (var reader in new[] { first, second })
        {
            var located = reader.Enrich([row]).Single();
            if (located.CountryCode != "US" || located.NetworkScope != "Internet")
                throw new Exception($"Live enrichment lost the country: {reader.CountryDataStatus}; code='{located.CountryCode}'.");
        }
        Console.WriteLine("PASS: simultaneous offline country readers locate Internet connections.");
    }
}
