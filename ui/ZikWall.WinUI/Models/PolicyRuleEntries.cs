namespace ZikWall.WinUI.Models;

public static class PolicyRuleEntries
{
    public static RuleEntry FromRule(FirewallRule rule) => new(
        rule.Name, rule.Application,
        rule.Group.Length > 0 ? rule.Group : rule.Application.Length > 0 ? "Applications" : "System rules",
        rule.Action,
        (rule.RemoteNetwork.Length == 0 ? "Any destination" : rule.RemoteNetwork) + (rule.RemotePort == 0 ? "" : $" · Port {rule.RemotePort}"),
        rule.Protocol == 6 ? "TCP" : rule.Protocol == 17 ? "UDP" : "Any",
        rule.Direction == 1 ? "Outbound" : rule.Direction == 2 ? "Inbound" : "Both",
        rule.Enabled, rule.Expires,
        "First matching rule wins within ZikWall. Other firewall providers can still block traffic.",
        PolicyRuleId: rule.Id);
}
