using ZikWall.WinUI.Models;

static class PolicyAnalysisTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
        var now = DateTimeOffset.UtcNow;
        var broad = new FirewallRule(Guid.NewGuid(), "Earlier", "", "192.0.2.1/24", 0, 0, 3, true, true, null);
        var narrow = new FirewallRule(Guid.NewGuid(), "Later", @"C:\Apps\test.exe", "192.0.2.15/32", 6, 443, 1, true, true, null);
        PolicyAnalysisResult Analyze(params FirewallRule[] rules) => PolicyAnalysis.Analyze(rules, now, _ => true);
        Check(Analyze(broad, narrow).Findings.Single().Summary.Contains("Redundant scope"), "Broader IP/protocol/direction coverage not detected.");
        Check(Analyze(broad, narrow with { Block = false }).Findings.Single().Summary.Contains("Overridden scope"), "Opposite action not identified.");
        Check(Analyze(broad with { Expires = now.AddMinutes(1) }, narrow).Findings.Single().Summary.Contains("Currently covered"), "Temporary predecessor must not imply permanent redundancy.");
        Check(Analyze(broad with { Expires = now.AddMinutes(1) }, narrow with { Expires = now.AddSeconds(30) }).Findings.Single().Summary.Contains("Redundant scope"), "Lifetime-contained redundancy not detected.");
        Check(Analyze(narrow, broad).Findings.Count == 0, "Narrow predecessor must not cover broad later rule.");
        Check(Analyze(broad with { RemoteNetwork = "192.0.3.0/24" }, narrow).Findings.Count == 0, "Separate networks cannot cover each other.");
        Check(Analyze(broad with { Direction = 2 }, narrow).Findings.Count == 0, "Inbound rule must not cover outbound rule.");
        Check(Analyze(broad with { Protocol = 17 }, narrow).Findings.Count == 0, "UDP rule must not cover TCP rule.");
        Check(Analyze(broad with { Protocol = 6, RemotePort = 80 }, narrow).Findings.Count == 0, "Ports must match for coverage.");
        Check(Analyze(broad with { Enabled = false }, narrow).Findings.Count == 1, "Disabled rule must not shadow later rules.");
        Check(Analyze(broad with { Expires = now }, narrow).Findings.Count == 1, "Expired rule must not shadow later rules.");
        Check(Analyze(narrow with { Application = @"c:\apps\TEST.exe" }, narrow).Findings.Count == 1, "Windows application paths must compare case-insensitively.");
        Check(Analyze(narrow with { Application = @"C:\Apps\other.exe" }, narrow).Findings.Count == 0, "Different applications must not cover each other.");
        Check(Analyze(broad with { RemoteNetwork = "2001:db8::/33" }, narrow with { RemoteNetwork = "2001:db8:1234::/48" }).Findings.Count == 1, "Non-byte IPv6 CIDR coverage failed.");
        Check(Analyze(broad with { RemoteNetwork = "2001:db8::/33" }, narrow with { RemoteNetwork = "2001:db8:ffff::/48" }).Findings.Count == 0, "Non-byte IPv6 CIDR boundary failed.");
        Check(Analyze(broad with { RemoteNetwork = "::/0" }, narrow).Findings.Count == 0, "IPv6 must not cover IPv4.");
        Check(PolicyAnalysis.Analyze([narrow], now, _ => false).Findings.Single().Summary.Contains("Executable unavailable"), "Missing or inaccessible executable must be identified conservatively.");
        Check(!Analyze(Enumerable.Range(0, 1001).Select(i => narrow with { Id = Guid.NewGuid(), Enabled = false }).ToArray()).Complete, "Truncated findings must be marked incomplete.");
        Check(!Analyze(Enumerable.Range(1, 1500).Select(i => narrow with { Id = Guid.NewGuid(), RemotePort = i }).ToArray()).Complete, "Comparison work must be bounded and partial results labeled.");
        Console.WriteLine($"PASS: {checks} rule-analysis checks.");
    }
}
