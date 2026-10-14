using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZikWall.WinUI.Models;

// Snapshots contain only representable rules. Unsupported native rules remain visible,
// but can never become a write request or silently lose their Windows-only conditions.
public sealed record WindowsFirewallEntry(string Name, string Description, string Group,
    FirewallRule? Rule, string Fingerprint, string Problem = "")
{
    public string Label => Name + (Problem.Length == 0 ? "" : " — " + Problem);
    public static WindowsFirewallEntry Create(string name, string description, string group, FirewallRule rule)
    {
        rule = rule with { RemoteNetwork = WindowsFirewallSync.NormalizeNetwork(rule.RemoteNetwork) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { name, description, group, rule.Name,
            Application = rule.Application.ToUpperInvariant(), rule.RemoteNetwork, rule.Protocol,
            rule.RemotePort, rule.Direction, rule.Block, rule.Enabled });
        return new(name, description, group, rule, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    public void ValidateWritable()
    {
        if (Rule is null || Problem.Length != 0 || Name.Length is < 1 or > 256 || Name.Any(char.IsControl)
            || Description.Length > 1024 || Group.Length > 256) throw new InvalidDataException("Unsupported Windows Firewall rule.");
        Rule.Validate();
        if (Rule.Expires is not null || Rule.Direction == 3) throw new InvalidDataException("Windows rules require one direction and no expiry.");
        if (Create(Name, Description, Group, Rule).Fingerprint != Fingerprint) throw new InvalidDataException("Invalid Windows rule snapshot.");
    }
}

public sealed record WindowsFirewallChange(WindowsFirewallEntry? Before, WindowsFirewallEntry? After);
public sealed record WindowsFirewallBatch(List<WindowsFirewallChange> Changes, bool Resume = false);
public sealed record WindowsFirewallLink(Guid RuleId, FirewallRule Local, List<WindowsFirewallEntry> Windows);
public sealed record WindowsFirewallPending(List<FirewallRule> Rules, List<WindowsFirewallLink> Links, List<WindowsFirewallChange> Changes);
public sealed record WindowsFirewallSyncState(Guid ProfileId, List<WindowsFirewallLink> Links, WindowsFirewallPending? Pending = null)
{
    public void Validate(PolicyDocument document)
    {
        if (!document.Profiles.Any(x => x.Id == ProfileId) || Links is null || Links.Count > 1000
            || Links.Select(x => x.RuleId).Distinct().Count() != Links.Count) throw new InvalidDataException("Invalid Windows Firewall sync state.");
        ValidateLinks(Links);
        if (Pending is { } pending)
        {
            if (pending.Rules is null || pending.Links is null || pending.Changes is null) throw new InvalidDataException("Invalid pending sync.");
            new PolicyDocument(1, ProfileId, false, [new(ProfileId, "Sync", pending.Rules)]).Validate();
            ValidateLinks(pending.Links);
            WindowsFirewallSync.ValidateBatch(new(pending.Changes));
        }
    }
    private static void ValidateLinks(List<WindowsFirewallLink> links)
    {
        if (links.Count > 1000 || links.Select(x => x.RuleId).Distinct().Count() != links.Count) throw new InvalidDataException("Duplicate sync links.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (link.Local is null || link.RuleId != link.Local.Id || link.Windows is null || link.Windows.Count is < 1 or > 2) throw new InvalidDataException("Invalid sync link.");
            link.Local.Validate();
            foreach (var entry in link.Windows) { entry.ValidateWritable(); if (!names.Add(entry.Name)) throw new InvalidDataException("Duplicate Windows rule link."); }
        }
    }
}

public enum WindowsFirewallConflictChoice { Review, ZikWall, Windows }
public sealed record WindowsFirewallPlan(List<FirewallRule> Rules, List<WindowsFirewallLink> Links,
    List<WindowsFirewallChange> Changes, List<string> Summary, List<string> Problems)
{
    public bool Ready => Problems.Count == 0;
}

public static class WindowsFirewallSync
{
    public const string Marker = "ZikWall sync v1:";
    public static string NormalizeNetwork(string value)
    {
        if (value is "" or "*") return "";
        var parts = value.Split('/');
        // Windows often returns an IPv4 subnet mask instead of a prefix length.
        if (parts.Length == 2 && IPAddress.TryParse(parts[1], out var mask) && mask.GetAddressBytes().Length == 4)
        {
            string bits = string.Concat(mask.GetAddressBytes().Select(x => Convert.ToString(x, 2).PadLeft(8, '0')));
            int prefix = bits.TakeWhile(x => x == '1').Count();
            if (bits.Skip(prefix).Any(x => x != '0')) throw new InvalidDataException("Non-contiguous subnet mask.");
            value = parts[0] + "/" + prefix;
        }
        var parsed = FirewallRule.ParseNetwork(value);
        var bytes = IPAddress.Parse(parsed.Address).GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++) bytes[i] &= (byte)(0xff << (8 - Math.Clamp((int)parsed.Prefix - i * 8, 0, 8)));
        return new IPAddress(bytes) + "/" + parsed.Prefix;
    }
    public static bool Equivalent(FirewallRule? a, FirewallRule? b) => a is null || b is null ? a is null && b is null :
        a.Name == b.Name && string.Equals(a.Application, b.Application, StringComparison.OrdinalIgnoreCase)
        && NormalizeNetwork(a.RemoteNetwork) == NormalizeNetwork(b.RemoteNetwork) && a.Protocol == b.Protocol
        && a.RemotePort == b.RemotePort && a.Direction == b.Direction && a.Block == b.Block && a.Enabled == b.Enabled && a.Expires == b.Expires;
    public static bool Same(WindowsFirewallEntry? a, WindowsFirewallEntry? b) => a is null || b is null ? a is null && b is null :
        a.Name == b.Name && a.Fingerprint == b.Fingerprint && a.Problem == b.Problem;
    public static string DisplayName(string name, string description)
    {
        if (!description.StartsWith(Marker, StringComparison.Ordinal)) return name;
        int suffix = name.LastIndexOf(" [ZikWall ", StringComparison.Ordinal);
        return suffix < 0 ? name : name[..suffix];
    }
    public static List<WindowsFirewallEntry> Find(WindowsFirewallEntry baseline, IReadOnlyList<WindowsFirewallEntry> current) =>
        current.Where(x => baseline.Description.StartsWith(Marker, StringComparison.Ordinal)
            ? x.Description == baseline.Description : string.Equals(x.Name, baseline.Name, StringComparison.OrdinalIgnoreCase)).ToList();

    private static List<WindowsFirewallEntry> Export(Guid profileId, FirewallRule rule, List<WindowsFirewallEntry> existing)
    {
        rule.Validate();
        if (rule.Expires is not null) throw new InvalidDataException($"{rule.Name}: temporary rules cannot be persisted in Windows Firewall. Remove the expiry or keep this rule outside the synced profile.");
        var result = new List<WindowsFirewallEntry>();
        foreach (int direction in new[] { 1, 2 }.Where(x => (rule.Direction & x) != 0))
        {
            var old = existing.FirstOrDefault(x => x.Rule?.Direction == direction);
            string description = old?.Description ?? $"{Marker}{profileId:N}:{rule.Id:N}:{direction}";
            string name = old is not null && !description.StartsWith(Marker, StringComparison.Ordinal) ? rule.Name :
                $"{rule.Name} [ZikWall {rule.Id:N}:{direction}]";
            result.Add(WindowsFirewallEntry.Create(name, description, old?.Group ?? "ZikWall", rule with { Direction = direction }));
        }
        return result;
    }

    private static FirewallRule? Import(Guid id, string group, List<WindowsFirewallEntry> entries)
    {
        if (entries.Count == 0) return null;
        foreach (var entry in entries) entry.ValidateWritable();
        var first = entries[0].Rule! with { Id = id, Group = group };
        if (entries.Count == 1) return first;
        if (entries.Count != 2 || entries[0].Rule!.Direction == entries[1].Rule!.Direction
            || !Equivalent(first with { Direction = 1 }, entries[1].Rule! with { Direction = 1 }))
            throw new InvalidDataException($"{first.Name}: its inbound and outbound Windows rules differ. Make them agree in Windows Firewall, or choose ZikWall for this conflict.");
        return first with { Direction = 3 };
    }

    public static WindowsFirewallPlan Plan(NetworkProfile profile, WindowsFirewallSyncState? state,
        IReadOnlyList<WindowsFirewallEntry> current, IEnumerable<WindowsFirewallEntry> imports,
        WindowsFirewallConflictChoice choice = WindowsFirewallConflictChoice.Review)
    {
        if (state is not null && state.ProfileId != profile.Id) throw new InvalidDataException("Sync is linked to another profile. Disconnect that profile first.");
        var rules = profile.Rules.ToList(); var links = new List<WindowsFirewallLink>();
        var changes = new List<WindowsFirewallChange>(); var summary = new List<string>(); var problems = new List<string>();
        var tracked = (state?.Links ?? []).Select(x => x.RuleId).ToHashSet();
        void Reconcile(FirewallRule? local, List<WindowsFirewallEntry> remote, Guid id)
        {
            var desired = local is null ? [] : Export(profile.Id, local, remote);
            var remaining = remote.ToList();
            foreach (var next in desired)
            {
                var before = remaining.FirstOrDefault(x => x.Rule?.Direction == next.Rule!.Direction);
                if (before is not null) remaining.Remove(before);
                if (!Same(before, next)) { changes.Add(new(before, next)); summary.Add($"Windows: {(before is null ? "create" : "update")} {next.Name}"); }
            }
            foreach (var removed in remaining) { changes.Add(new(removed, null)); summary.Add($"Windows: delete {removed.Name}"); }
            if (local is not null) links.Add(new(id, local, desired));
        }
        foreach (var link in state?.Links ?? [])
        {
            try
            {
                var local = rules.SingleOrDefault(x => x.Id == link.RuleId);
                var remote = new List<WindowsFirewallEntry>();
                foreach (var before in link.Windows)
                {
                    var matches = Find(before, current);
                    if (matches.Count > 1) throw new InvalidDataException($"{before.Name}: multiple Windows rules share its identity.");
                    remote.AddRange(matches);
                }
                bool localChanged = !Equivalent(local, link.Local);
                bool remoteChanged = remote.Count != link.Windows.Count || remote.Where((x, i) => !Same(x, link.Windows[i])).Any();
                // Advanced Windows conditions must never be removed by a conflict choice.
                foreach (var entry in remote) entry.ValidateWritable();
                FirewallRule? imported = null; string? conversionProblem = null;
                try { imported = Import(link.RuleId, local?.Group ?? link.Local.Group, remote); }
                catch (InvalidDataException ex) { conversionProblem = ex.Message; }
                bool conflict = conversionProblem is not null || (localChanged && remoteChanged && !Equivalent(local, imported));
                if (conflict && choice == WindowsFirewallConflictChoice.Review)
                    throw new InvalidDataException(conversionProblem ?? $"{link.Local.Name}: changed in both apps (including deletion). Choose which version to keep.");
                if (conversionProblem is not null && choice != WindowsFirewallConflictChoice.ZikWall) throw new InvalidDataException(conversionProblem);
                if (remoteChanged && (!localChanged || Equivalent(local, imported)) && !conflict || conflict && choice == WindowsFirewallConflictChoice.Windows)
                {
                    int index = rules.FindIndex(x => x.Id == link.RuleId);
                    if (index >= 0) { if (imported is null) rules.RemoveAt(index); else rules[index] = imported; }
                    else if (imported is not null) rules.Add(imported);
                    if (imported is not null) links.Add(new(link.RuleId, imported, remote));
                    summary.Add($"ZikWall: {(imported is null ? "delete" : "update")} {link.Local.Name}");
                }
                else if (localChanged || conflict) Reconcile(local, remote, link.RuleId);
                else links.Add(link with { Local = local ?? link.Local });
            }
            catch (InvalidDataException ex) { problems.Add(ex.Message); }
        }
        foreach (var rule in profile.Rules.Where(x => !tracked.Contains(x.Id)))
        {
            try { Reconcile(rule, [], rule.Id); }
            catch (InvalidDataException ex) { problems.Add(ex.Message); }
        }
        var linkedNames = (state?.Links ?? []).SelectMany(x => x.Windows).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in imports.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (linkedNames.Contains(entry.Name)) continue;
                if (current.Count(x => string.Equals(x.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) != 1) throw new InvalidDataException($"{entry.Name}: duplicate Windows rule names cannot be synced.");
                if (entry.Description.StartsWith(Marker, StringComparison.Ordinal)) throw new InvalidDataException($"{entry.Name}: already owned by a ZikWall sync profile.");
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Id + "|" + entry.Name))[..16]);
                var imported = Import(id, "Custom rules", [entry])!;
                if (rules.Any(x => x.Id == id)) throw new InvalidDataException("Imported rule identity collision.");
                rules.Add(imported); links.Add(new(id, imported, [entry])); summary.Add($"ZikWall: import {entry.Name}");
            }
            catch (InvalidDataException ex) { problems.Add($"{entry.Name}: {ex.Message}"); }
        }
        // Block precedence differs from ZikWall ordering. Conservatively reject any
        // intersecting allow/block pair rather than silently changing its meaning.
        for (int i = 0; i < rules.Count; i++)
            for (int j = i + 1; j < rules.Count; j++)
                if (rules[i].Enabled && rules[j].Enabled && rules[i].Block != rules[j].Block && Overlaps(rules[i], rules[j]))
                { problems.Add($"{rules[i].Name} / {rules[j].Name}: overlapping allow/block rules have different precedence in Windows Firewall."); if (problems.Count >= 100) break; }
        if (links.Count > 1000 || changes.Count > 1000) problems.Add("Sync supports at most 1,000 linked rules and 1,000 Windows changes per operation.");
        foreach (var change in changes.Where(x => x.After is not null))
            if (current.Any(x => string.Equals(x.Name, change.After!.Name, StringComparison.OrdinalIgnoreCase) && !Same(x, change.Before)))
                problems.Add($"{change.After!.Name}: another Windows rule already uses this name.");
        return new(rules, links, changes, summary, problems.Distinct().ToList());
    }
    private static bool Overlaps(FirewallRule a, FirewallRule b)
    {
        if ((a.Direction & b.Direction) == 0 || a.Protocol != 0 && b.Protocol != 0 && a.Protocol != b.Protocol
            || a.RemotePort != 0 && b.RemotePort != 0 && a.RemotePort != b.RemotePort
            || a.Application.Length > 0 && b.Application.Length > 0 && !string.Equals(a.Application, b.Application, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.RemoteNetwork.Length == 0 || b.RemoteNetwork.Length == 0) return true;
        var x = FirewallRule.ParseNetwork(a.RemoteNetwork); var y = FirewallRule.ParseNetwork(b.RemoteNetwork);
        var xb = IPAddress.Parse(x.Address).GetAddressBytes(); var yb = IPAddress.Parse(y.Address).GetAddressBytes();
        if (xb.Length != yb.Length) return false;
        int bits = (int)Math.Min(x.Prefix, y.Prefix);
        for (int i = 0; i < xb.Length; i++) { int mask = 0xff << (8 - Math.Clamp(bits - i * 8, 0, 8)); if ((xb[i] & mask) != (yb[i] & mask)) return false; }
        return true;
    }
    public static void ValidateBatch(WindowsFirewallBatch batch)
    {
        if (batch.Changes is null || batch.Changes.Count > 1000) throw new InvalidDataException("Too many Windows changes.");
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var after = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in batch.Changes)
        {
            if (change is null || change.Before is null && change.After is null) throw new InvalidDataException("Empty Windows change.");
            if (change.Before is { } b) { b.ValidateWritable(); if (!before.Add(b.Name)) throw new InvalidDataException("Duplicate Windows change."); }
            if (change.After is { } a) { a.ValidateWritable(); if (!after.Add(a.Name)) throw new InvalidDataException("Duplicate Windows destination."); }
        }
        foreach (var change in batch.Changes)
            if (change.After is { } a && before.Contains(a.Name) && !string.Equals(a.Name, change.Before?.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Windows rule name swaps must be synced separately.");
    }
}
