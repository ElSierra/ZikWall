using System.Net;

namespace ZikWall.WinUI.Models;

public sealed record PolicyFinding(Guid RuleId, string Summary);
public sealed record PolicyAnalysisResult(IReadOnlyList<PolicyFinding> Findings, bool Complete);

/// <summary>Conservative, read-only analysis of a saved policy snapshot, not observed WFP decisions.</summary>
public static class PolicyAnalysis
{
    private sealed record Network(byte[] Address, uint Prefix);
    private sealed record Entry(FirewallRule Rule, Network? Network, int Position);
    public static PolicyAnalysisResult Analyze(IReadOnlyList<FirewallRule> rules, DateTimeOffset now,
        Func<string, bool> executableExists)
    {
        const int maximumComparisons = 1_000_000, maximumFindings = 1000;
        var findings = new List<PolicyFinding>();
        var previous = new List<Entry>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int comparisons = 0;
        bool complete = true;
        for (int index = 0; index < rules.Count; index++)
        {
            if (findings.Count >= maximumFindings) { complete = false; break; }
            var rule = rules[index];
            void Add(string description) => findings.Add(new(rule.Id, $"{index + 1}. {rule.Name}\n{description}"));
            try { rule.Validate(); }
            catch (InvalidDataException ex) { Add($"Invalid rule: {ex.Message}"); continue; }
            if (!rule.Enabled) { Add("Disabled: this rule is not sent to Windows."); continue; }
            if (!rule.IsActive(now)) { Add("Expired: this rule is not sent to Windows."); continue; }
            if (rule.Application.Length > 0)
            {
                if (!paths.TryGetValue(rule.Application, out bool exists))
                    paths[rule.Application] = exists = executableExists(rule.Application);
                if (!exists) Add("Executable unavailable at the time of this check. It may have moved, been removed or be inaccessible; verify the path before deleting its rule.");
            }
            var parsed = FirewallRule.ParseNetwork(rule.RemoteNetwork);
            var entry = new Entry(rule, parsed.Address.Length == 0 ? null : new(IPAddress.Parse(parsed.Address).GetAddressBytes(), parsed.Prefix), index + 1);
            foreach (var earlier in previous)
            {
                if (++comparisons > maximumComparisons) { complete = false; break; }
                if (!Covers(earlier, entry)) continue;
                bool entireLifetime = earlier.Rule.Expires is null || (rule.Expires is { } end && earlier.Rule.Expires >= end);
                string description = earlier.Rule.Block == rule.Block
                    ? "Redundant scope: an earlier rule has the same action and covers this entire rule."
                    : "Overridden scope: an earlier rule has the opposite action and covers this entire rule.";
                if (!entireLifetime)
                    description = "Currently covered by an earlier rule. This rule can take effect when that earlier rule expires.";
                Add($"{description} Earlier rule: {earlier.Position}. {earlier.Rule.Name}." +
                    (earlier.Rule.Expires is { } expiry ? $" Earlier rule expires {expiry.ToLocalTime():g}." : ""));
                break;
            }
            if (!complete) break;
            previous.Add(entry);
        }
        return new(findings, complete);
    }
    private static bool Covers(Entry earlier, Entry later)
    {
        var a = earlier.Rule; var b = later.Rule;
        if (a.Application.Length != 0 && !string.Equals(a.Application, b.Application, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Protocol != 0 && a.Protocol != b.Protocol) return false;
        if (a.RemotePort != 0 && a.RemotePort != b.RemotePort) return false;
        if ((a.Direction & b.Direction) != b.Direction) return false;
        if (earlier.Network is null) return true;
        if (later.Network is null) return false;
        var first = earlier.Network; var second = later.Network;
        if (first.Address.Length != second.Address.Length || first.Prefix > second.Prefix) return false;
        int whole = (int)first.Prefix / 8, remainder = (int)first.Prefix % 8;
        for (int i = 0; i < whole; i++) if (first.Address[i] != second.Address[i]) return false;
        int mask = (0xff << (8 - remainder)) & 0xff;
        return remainder == 0 || (first.Address[whole] & mask) == (second.Address[whole] & mask);
    }
}
