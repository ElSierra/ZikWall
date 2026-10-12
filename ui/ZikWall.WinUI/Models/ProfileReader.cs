using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ZikWall.WinUI.Models;

// Parse only. Never load libraries, resolve network paths, execute content, or rewrite profiles.
public static class ProfileReader
{
    public const long MaximumBytes = 8 * 1024 * 1024;
    public static IReadOnlyList<RuleEntry> Read(Stream stream)
    {
        if (stream.CanSeek && stream.Length > MaximumBytes) throw new InvalidDataException("The profile exceeds the 8 MB limit.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
            IgnoreComments = true
        });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name != "root") throw new InvalidDataException("This is not a ZikWall XML profile.");
        var result = new List<RuleEntry>();
        foreach (var section in doc.Root.Elements())
        {
            var category = section.Name.LocalName switch
            {
                "apps" => "Applications", "rules_custom" => "Custom rules",
                "rules_system" => "System rules", "rules_blocklist" => "Blocklists", _ => null
            };
            if (category is null) continue;
            foreach (var item in section.Elements("item"))
            {
                string Get(string key, string fallback = "") => (string?)item.Attribute(key) ?? fallback;
                bool Flag(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase) || Get(key) == "1";
                var path = Get("path");
                var name = Get("name", path.Split('\\', '/').LastOrDefault() ?? "Application");
                if (string.IsNullOrWhiteSpace(name)) name = "Unnamed entry";
                var enabled = Flag("is_enabled");
                // An unchecked application has no blanket permission; that is not an explicit block rule.
                var action = category == "Applications" ? "Allow" : Flag("is_block") ? "Block" : "Allow";
                var protocol = Get("protocol") switch { "6" => "TCP", "17" => "UDP", "1" => "ICMP", "58" => "ICMPv6", "" or "0" => "Any", var value => value };
                var direction = Get("dir") switch { "1" => "Inbound", "2" => "Both", _ => "Outbound" };
                DateTimeOffset? expires = null;
                if (long.TryParse(Get("timer"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp) && timestamp > 0)
                {
                    try { expires = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
                    catch (ArgumentOutOfRangeException) { throw new InvalidDataException("A timer value is outside the supported range."); }
                }
                var endpoint = Get("rule", Get("rule_remote", "Any destination"));
                if (endpoint.Length == 0) endpoint = "Any destination";
                result.Add(new(name, path, category, action, endpoint, protocol, direction, enabled, expires,
                    Get("comment"), Get("rule_local", "Any"), Get("apps")));
                if (result.Count > 50000) throw new InvalidDataException("The profile exceeds the 50,000-entry limit.");
            }
        }
        if (result.Count == 0) throw new InvalidDataException("No supported application or rule entries were found. Open a profile.xml or profile_internal.xml file.");
        return result;
    }
}
