using System.Net;

namespace ZikWall.WinUI.Models;

public sealed record HostEntry(string Address, string Names, string Flag, int Connections, string Source)
{
    public string Label => Names.Length == 0 ? Address : Names.Split(", ")[0];
    public string Details => $"{Address} · {Connections} {(Connections == 1 ? "connection" : "connections")}";
    public string Description => Names.Length == 0 ? Details : $"{Names}\n{Details}\n{Source}; may not be the website requested by this app.";
}

// Group by remote address, never by a DNS hint: multiple sockets and aliases
// must not inflate the host count or change a firewall target.
public sealed record HostSummary(IReadOnlyList<HostEntry> Items, int Count)
{
    public static HostSummary Empty { get; } = new([], 0);
    public string Primary => Items.FirstOrDefault()?.Label ?? "—";
    public string Flag => Items.FirstOrDefault()?.Flag ?? "ms-appx:///Assets/Flags/unknown.svg";
    public string More => Count > 1 ? $"+{Count - 1} more" : "";
    public string Title => $"{Count} {(Count == 1 ? "host" : "hosts")}" + (Count > Items.Count ? $" · first {Items.Count} shown" : "");
    public string Description => Items.Count == 0 ? "No remote host (local endpoint or listener)." : Items[0].Description;
    public bool Available => Count > 0;

    public static HostSummary Create(IEnumerable<ConnectionEntry> entries)
    {
        var hosts = entries.Where(x => x.RemotePort is > 0)
            .GroupBy(x => Canonical(x.RemoteAddress), StringComparer.Ordinal)
            .Select(group => new HostEntry(group.Key,
                string.Join(", ", group.SelectMany(x => x.HostNames.Split(", ", StringSplitOptions.RemoveEmptyEntries))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
                group.First().CountryFlag, group.Select(x => x.Identity).Distinct().Count(),
                string.Join(" / ", group.Select(x => x.HostNameSource).Distinct())) )
            .OrderBy(x => x.Names.Length == 0).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Address, StringComparer.Ordinal).ToArray();
        return hosts.Length == 0 ? Empty : new(hosts.Take(128).ToArray(), hosts.Length);
    }

    private static string Canonical(string address) => IPAddress.TryParse(address, out var ip)
        ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : address;
    public bool SameContent(HostSummary other) => Count == other.Count && Items.SequenceEqual(other.Items);
}
