using System.Text;
using System.Xml;
using ZikWall.WinUI.Models;

if (args.Contains("--activity-overview-only")) { ActivityOverviewTests.Run(); return; }
if (args.Contains("--notifications-only")) { NotificationTests.Run(); return; }
if (args.Contains("--passive-dns-only")) { await PassiveDnsTests.RunAsync(); return; }

if (args.SequenceEqual(new[] { "--log-analysis" })) { LogAnalysisTests.Run(); return; }

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
IReadOnlyList<RuleEntry> Read(string xml) => ProfileReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
void Reject(string xml, string message)
{
    try { Read(xml); }
    catch (Exception ex) when (ex is XmlException or InvalidDataException) { checks++; return; }
    throw new Exception(message);
}

var profile = Read("""
<root type="3" version="6"><apps>
<item path="C:\Apps\browser.exe" is_enabled="true" />
<item path="C:\Apps\disabled.exe" />
</apps><rules_custom>
<item name="Block telemetry" rule="192.0.2.0/24" rule_local="1234" apps="browser.exe" is_block="true" is_enabled="true" protocol="6" dir="1" comment="test" />
</rules_custom></root>
""");
Check(profile.Count == 3, "Application and custom rules must load.");
Check(profile[0].Name == "browser.exe", "Display name must come from the Windows path.");
Check(profile[1].State == "Disabled", "Unchecked applications must not be represented as explicit block rules.");
Check(profile[2].Action == "Block" && profile[2].Direction == "Inbound" && profile[2].Protocol == "TCP", "Rule metadata must match ZikWall.");
Check(profile[2].LocalEndpoint == "1234" && profile[2].Applications == "browser.exe", "Scope must survive parsing.");
Check(RuleQuery.Filter(profile,"blocked","").Single() == profile[2], "Blocked filter must exclude disabled blanket permissions.");
Check(RuleQuery.Filter(profile,"allowed","BROWSER").Single() == profile[0], "Search must intersect the category filter case-insensitively.");
Check(RuleQuery.Filter(profile,"all","192.0.2").Count() == 1, "Endpoint search must work.");
Check(RuleQuery.Filter(profile,"all","missing").Count() == 0, "Empty results must be represented honestly.");
var timers = new[] { profile[0] with { Expires = DateTimeOffset.UtcNow.AddMinutes(5) }, profile[1] with { Expires = DateTimeOffset.UtcNow.AddMinutes(-5) } };
Check(RuleQuery.Filter(timers,"temporary","").Count() == 1, "Expired timers must not be shown as temporary access.");
Check(RuleQuery.Filter(timers,"allowed","").Count() == 1 && timers[1].State == "Expired", "Expired permissions must not appear active.");
Check(!((timers[0] with { Enabled = false }).IsTemporary), "A disabled entry must not be shown as temporary access.");
Reject("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><root><apps><item path='&x;'/></apps></root>", "DTD/external entities must be rejected.");
Reject("<html/>", "Unrelated XML must be rejected.");
Reject("<root><apps><item timer='9223372036854775807'/></apps></root>", "Out of range timestamps must be rejected.");
Reject("<root><apps>", "Malformed XML must be rejected.");
using (var oversized = new MemoryStream(new byte[ProfileReader.MaximumBytes + 1]))
{
    try { ProfileReader.Read(oversized); throw new Exception("Oversized input was accepted."); }
    catch (InvalidDataException) { checks++; }
}
if (args.Length > 0)
{
    using var input = File.OpenRead(args[0]);
    var before = System.Security.Cryptography.SHA256.HashData(input);
    input.Position = 0;
    var actual = ProfileReader.Read(input);
    Check(actual.Any(x => x.Category == "System rules") && actual.Count > 20, "The repository's real internal profile must load.");
    input.Position = 0;
    Check(before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(input)), "Profile loading must not modify the source.");
    Console.WriteLine($"Read {actual.Count} real internal rules without modifying the file.");
}
Console.WriteLine($"PASS: {checks} model and profile-reader checks.");
MonitorTests.Run();
MonitorEnrichmentTests.Run();
NotificationTests.Run();
SystemAppTests.Run();
PreferencesTests.Run();
await CachedDomainTests.RunAsync(args.Contains("--dns-cache"));
await HostNameTests.RunAsync();
await PassiveDnsTests.RunAsync();
PolicyTests.Run();
PolicyAnalysisTests.Run();
PrivacyTests.RunAsync().GetAwaiter().GetResult();
await SubscriptionTests.RunAsync(args.Contains("--subscriptions"));
if (args.Contains("--doh")) PrivacyTests.TestDohAsync().GetAwaiter().GetResult();
ActivityUsageTests.Run();
ActivityOverviewTests.Run();
MonitorActionTests.Run();
LogAnalysisTests.Run();
