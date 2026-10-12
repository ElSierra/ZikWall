using System.Globalization;
using System.Net;

namespace ZikWall.WinUI.Models;

public sealed record BlocklistData(List<string> Networks, List<string> Domains, int UnsupportedLines);
public sealed record BlocklistSubscription(Guid Id, string Name, string Url, string ExpectedSha256, DateTimeOffset? Updated, BlocklistData Data);
public sealed record PrivacySettings(List<BlocklistSubscription> Subscriptions, List<string> AllowedDomains, bool AutomaticUpdates)
{
    public static PrivacySettings Empty() => new([], [], false);
}
public static class BlocklistParser
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static string? Domain(string value)
    {
        value = value.Trim().TrimEnd('.');
        try { value = new IdnMapping().GetAscii(value).ToLowerInvariant(); } catch (ArgumentException) { return null; }
        if (value.Length is < 1 or > 253 || IPAddress.TryParse(value, out _)) return null;
        var labels = value.Split('.');
        if (labels.Length < 2 || labels.Any(x => x.Length is < 1 or > 63 || x[0] == '-' || x[^1] == '-' || x.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))) return null;
        return value;
    }
    public static BlocklistData Parse(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Blocklist exceeds 8 MB.");
        var networks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int skipped = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            line = line.Split('#')[0].Trim();
            if (line.Length == 0 || line.StartsWith('!') || line.StartsWith('[')) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 1 && fields[0] is "0.0.0.0" or "127.0.0.1" or "::")
            {
                foreach (string field in fields.Skip(1)) { if (Domain(field) is { } domain) domains.Add(domain); else if (field != "localhost") skipped++; }
            }
            else if (fields.Length == 1)
            {
                string value = fields[0];
                if (value.StartsWith("||") && value.EndsWith('^')) value = value[2..^1];
                if (Domain(value) is { } domain) domains.Add(domain);
                else
                {
                    try
                    {
                        var (address, prefix) = FirewallRule.ParseNetwork(value);
                        if (address.Length == 0 || prefix == 0) { skipped++; continue; }
                        networks.Add($"{address}/{prefix}");
                    }
                    catch (InvalidDataException) { skipped++; }
                }
            }
            else skipped++;
            if (domains.Count > 100000 || networks.Count > 10000) throw new InvalidDataException("Blocklist exceeds 100,000 domains or 10,000 networks.");
        }
        if (domains.Count == 0 && networks.Count == 0) throw new InvalidDataException("No supported domains or IP networks were found.");
        return new(networks.Order().ToList(), domains.Order().ToList(), skipped);
    }
    public static bool MatchesDomain(string query, IReadOnlySet<string> blocked, IReadOnlySet<string> allowed)
    {
        string name = query.TrimEnd('.').ToLowerInvariant();
        for (string current = name; current.Length > 0; current = Parent(current)) if (allowed.Contains(current)) return false;
        for (string current = name; current.Length > 0; current = Parent(current)) if (blocked.Contains(current)) return true;
        return false;
    }
    private static string Parent(string value) { int dot = value.IndexOf('.'); return dot < 0 ? "" : value[(dot + 1)..]; }
}
