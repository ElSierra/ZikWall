using System.Buffers.Binary;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class PolicyTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
        void Reject(FirewallRule rule) { try { rule.Validate(); throw new Exception("Unsafe rule accepted."); } catch (InvalidDataException) { checks++; } }
        var rule = new FirewallRule(Guid.NewGuid(), "Loopback test", @"C:\Tests\fixture.exe", "127.0.0.1/32", 6, 443, 1, true, true, null);
        rule.Validate(); checks++;
        var row = PolicyRuleEntries.FromRule(rule);
        Check(row.PolicyRuleId == rule.Id && row.Path == rule.Application && row.Destination == "127.0.0.1/32 · Port 443" && row.Protocol == "TCP" && row.Direction == "Outbound", "The rule browser must preserve identity and enforcement scope.");
        Check(row.Category == "Applications" && row.Duration == "While filtering is enabled", "Saved application rules must show their session lifetime.");
        var network = rule with { Application = "", Group = "System rules" };
        Check(RuleQuery.Filter([PolicyRuleEntries.FromRule(network)], "system", "443").Count() == 1, "System rules must filter and search the saved policy.");
        var blocklist = rule with { Application = "", Group = "Blocklists" };
        Check(RuleQuery.Filter([PolicyRuleEntries.FromRule(blocklist)], "blocklists", "").Count() == 1, "Blocklist membership must survive projection.");
        Check(!RuleQuery.Filter([PolicyRuleEntries.FromRule(rule with { Enabled = false })], "blocked", "").Any(), "Disabled saved rules must not appear in the enabled blocked view.");
        Check(!RuleQuery.Filter([PolicyRuleEntries.FromRule(rule with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) })], "temporary", "").Any(), "Expired saved rules must leave the temporary view.");
        Reject(rule with { Group = "Unknown" });
        Reject(rule with { Group = null! });
        Check(FirewallRule.ParseNetwork("2001:db8::/32") == ("2001:db8::", 32u), "IPv6 CIDR parsing failed.");
        Reject(rule with { Application = "", RemoteNetwork = "" });
        Reject(rule with { Application = "relative.exe" });
        Reject(rule with { Application = @"\\server\app.exe" });
        Reject(rule with { RemoteNetwork = "example.com" });
        Reject(rule with { RemoteNetwork = "127.0.0.1/33" });
        Reject(rule with { RemoteNetwork = "fe80::1%12" });
        Reject(rule with { RemotePort = 65536 });
        Reject(rule with { Protocol = 0, RemotePort = 443 });
        Reject(rule with { Direction = 0 });
        var now = DateTimeOffset.UtcNow;
        Check(!(rule with { Expires = now }).IsActive(now) && !(rule with { Enabled = false }).IsActive(now), "Expired and disabled rules must not reach WFP.");
        string directory = Path.Combine(Path.GetTempPath(), "ZikWall-policy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PolicyStore(directory);
            var policy = PolicyDocument.Empty(); policy.Profiles[0].Rules.Add(rule); store.Save(policy);
            Check(store.Load().Profiles[0].Rules.Single() == rule, "Policy must survive a restart.");
            policy.Profiles[0].Rules[0] = blocklist;
            store.Save(policy);
            Check(store.Load().Profiles[0].Rules.Single().Group == "Blocklists", "Rule groups must persist across restart.");
            policy.Profiles[0].Rules[0] = rule;
            store.Save(policy);
            var invalid = policy with { SelectedProfile = Guid.NewGuid() };
            try { store.Save(invalid); throw new Exception("Invalid policy persisted."); } catch (InvalidDataException) { checks++; }
            Check(store.Load().SelectedProfile == policy.SelectedProfile, "A failed save must preserve the previous policy.");
            var duplicate = policy with { Profiles = [policy.Profiles[0], policy.Profiles[0] with { Id = Guid.NewGuid(), NetworkId = "network" }] };
            duplicate.Profiles[0] = duplicate.Profiles[0] with { NetworkId = "network" };
            try { duplicate.Validate(); throw new Exception("Duplicate network association accepted."); } catch (InvalidDataException) { checks++; }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        using var stream = new MemoryStream();
        BrokerWire.WriteAsync(stream, new BrokerRequest("apply", [rule]), CancellationToken.None).GetAwaiter().GetResult(); stream.Position = 0;
        var read = BrokerWire.ReadAsync<BrokerRequest>(stream, CancellationToken.None).GetAwaiter().GetResult();
        Check(read.Rules?.Single() == rule, "IPC policy round trip failed.");
        try { BrokerWire.ValidateSize(new BrokerRequest(new string('x', BrokerWire.MaximumBytes))); throw new Exception("Oversized local IPC accepted."); } catch (InvalidDataException) { checks++; }
        foreach (int size in new[] { -1, 0, BrokerWire.MaximumBytes + 1 })
        {
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, size);
            using var bad = new MemoryStream(header);
            try { BrokerWire.ReadAsync<BrokerRequest>(bad, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Oversized IPC accepted."); } catch (InvalidDataException) { checks++; }
        }
        if (OperatingSystem.IsWindows()) { _ = NetworkIdentity.Read(); Console.WriteLine("Windows Network List Manager read completed."); checks++; }
        Console.WriteLine($"PASS: {checks} policy, persistence and IPC checks.");
        return checks;
    }
}
