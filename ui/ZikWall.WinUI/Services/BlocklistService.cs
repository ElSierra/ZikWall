using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public static class BlocklistService
{
    public static async Task<BlocklistSubscription> DownloadAsync(BlocklistSubscription subscription, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await DownloadAsync(subscription, client, token);
    }
    internal static async Task<BlocklistSubscription> DownloadAsync(BlocklistSubscription subscription, HttpClient client, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20)); token = timeout.Token;
        if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) throw new InvalidDataException("Use an HTTPS URL without credentials or a fragment.");
        string expected = subscription.ExpectedSha256.Trim();
        if (expected.Length > 0 && (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))) throw new InvalidDataException("An optional SHA-256 pin must contain 64 hexadecimal characters.");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > BlocklistParser.MaximumBytes) throw new InvalidDataException("Blocklist exceeds 8 MB.");
        using var body = await response.Content.ReadAsStreamAsync(token);
        var bytes = await ReadBoundedAsync(body, BlocklistParser.MaximumBytes, token);
        if (expected.Length > 0 && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 pin mismatch. The previous list was retained.");
        var parsed = BlocklistParser.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        return subscription with { Data = parsed, Updated = DateTimeOffset.UtcNow };
    }
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > maximum) throw new InvalidDataException("Response exceeds the size limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}

public sealed class PrivacyStore(string directory)
{
    private string FilePath => Path.Combine(directory, "privacy.json");
    public PrivacySettings Load()
    {
        if (!File.Exists(FilePath)) return PrivacySettings.Empty();
        if (new FileInfo(FilePath).Length > 24 * 1024 * 1024) throw new InvalidDataException("Privacy settings exceed 24 MB.");
        var settings = JsonSerializer.Deserialize<PrivacySettings>(File.ReadAllBytes(FilePath)) ?? throw new InvalidDataException("Empty privacy settings.");
        Validate(settings); return settings;
    }
    private static void Validate(PrivacySettings settings)
    {
        if (settings.Subscriptions is null || settings.AllowedDomains is null || settings.Subscriptions.Count > 16 || settings.AllowedDomains.Count > 10000
            || settings.AllowedDomains.Any(x => x is null || BlocklistParser.Domain(x) != x)
            || settings.Subscriptions.Any(x => x is null || x.Data is null || x.Data.Domains is null || x.Data.Networks is null || x.Data.Domains.Count > 100000 || x.Data.Networks.Count > 10000 || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 128 || x.Url is null || x.ExpectedSha256 is null)) throw new InvalidDataException("Invalid privacy settings.");
        foreach (var item in settings.Subscriptions)
        {
            if (item.Data.Domains.Any(x => x is null || BlocklistParser.Domain(x) != x)) throw new InvalidDataException("Invalid saved domain.");
            foreach (var network in item.Data.Networks) { if (network is null) throw new InvalidDataException("Invalid saved network."); FirewallRule.ParseNetwork(network); }
        }
    }
    public void Save(PrivacySettings settings)
    {
        Validate(settings); var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        if (bytes.Length > 24 * 1024 * 1024) throw new InvalidDataException("Privacy settings exceed 24 MB.");
        Directory.CreateDirectory(directory); string temporary = FilePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, FilePath, true);
    }
}
