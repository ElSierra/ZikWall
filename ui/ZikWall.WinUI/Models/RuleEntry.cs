namespace ZikWall.WinUI.Models;

public sealed record RuleEntry(
    string Name, string Path, string Category, string Action, string Destination,
    string Protocol = "Any", string Direction = "Outbound", bool Enabled = true,
    DateTimeOffset? Expires = null, string Notes = "", string LocalEndpoint = "Any", string Applications = "", Guid? PolicyRuleId = null)
{
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool IsActive => Enabled && (Expires is null || Expires > DateTimeOffset.UtcNow);
    public string State => Expires <= DateTimeOffset.UtcNow ? "Expired" : !Enabled ? "Disabled" : Action;
    public string Scope => Category == "Applications" ? "Application" : Category;
    public string Duration => Expires is null ? PolicyRuleId is null ? "Until changed" : "While filtering is enabled" : Expires <= DateTimeOffset.UtcNow ? "Expired" : $"Until {Expires.Value.ToLocalTime():g}";
    public bool IsTemporary => IsActive && Expires > DateTimeOffset.UtcNow;
    public string SearchText => $"{Name} {Path} {Destination} {Protocol} {Notes} {Applications} {LocalEndpoint}";
    public override string ToString() => $"{Name}, {State}, {Destination}";
}

public static class RuleQuery
{
    public static IEnumerable<RuleEntry> Filter(IEnumerable<RuleEntry> source, string view, string query) => source
        .Where(item => view switch
        {
            "allowed" => item.IsActive && item.Action == "Allow",
            "blocked" => item.IsActive && item.Action == "Block",
            "temporary" => item.IsTemporary,
            "system" => item.Category == "System rules",
            "blocklists" => item.Category == "Blocklists",
            _ => true
        })
        .Where(item => string.IsNullOrWhiteSpace(query) || item.SearchText.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
}
