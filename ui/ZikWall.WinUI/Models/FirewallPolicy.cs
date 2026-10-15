using System.Net;

namespace ZikWall.WinUI.Models;

public sealed record FirewallRule(Guid Id, string Name, string Application, string RemoteNetwork, int Protocol,
    int RemotePort, int Direction, bool Block, bool Enabled, DateTimeOffset? Expires)
{
    public string Group { get; init; } = "";
    public string Action => Block ? "Block" : "Allow";
    public string Scope => $"{(Application.Length == 0 ? "All applications" : Path.GetFileName(Application))} · {(RemoteNetwork.Length == 0 ? "Any IP" : RemoteNetwork)} · {(Protocol == 6 ? "TCP" : Protocol == 17 ? "UDP" : "Any protocol")}{(RemotePort == 0 ? "" : $":{RemotePort}")}";
    public string Lifetime => !Enabled ? "Disabled" : Expires is { } expiry ? expiry <= DateTimeOffset.UtcNow ? "Expired" : $"Expires {expiry.ToLocalTime():g}" : "While filtering is enabled";
    public bool IsActive(DateTimeOffset now) => Enabled && (Expires is null || Expires > now);
    public void Validate()
    {
        if (Group is not ("" or "Applications" or "Custom rules" or "System rules" or "Blocklists")) throw new InvalidDataException("Invalid rule group.");
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Name.Any(char.IsControl)) throw new InvalidDataException("Give the rule a name of 1–128 characters.");
        if (Application is null || RemoteNetwork is null) throw new InvalidDataException("Missing rule scope.");
        if (Application.Length == 0 && RemoteNetwork.Length == 0) throw new InvalidDataException("Choose an application or a remote IP/network; unscoped global rules are not supported.");
        if (Application.Length > 0 && (!Path.IsPathFullyQualified(Application) || Application.StartsWith(@"\\") || !Application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Application.Contains('\0') || Application.Length > 32760))
            throw new InvalidDataException("Use a fully qualified local .exe path.");
        ParseNetwork(RemoteNetwork);
        if (Protocol is not (0 or 6 or 17) || RemotePort is < 0 or > 65535 || (RemotePort != 0 && Protocol == 0) || Direction is < 1 or > 3) throw new InvalidDataException("Invalid protocol, remote port or direction. Ports require TCP or UDP.");
    }
    public static (string Address, uint Prefix) ParseNetwork(string network)
    {
        if (network == "") return ("", 0);
        if (network.Length > 64) throw new InvalidDataException("Use one IPv4/IPv6 address or CIDR network.");
        var parts = network.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || address.IsIPv4MappedToIPv6 || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)) throw new InvalidDataException("Use an IPv4/IPv6 address or CIDR without a scope ID.");
        uint bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32u : 128u;
        uint prefix = bits;
        if (parts.Length == 2 && (!uint.TryParse(parts[1], out prefix) || prefix > bits)) throw new InvalidDataException("Invalid CIDR prefix length.");
        return (address.ToString(), prefix);
    }
}

public sealed record NetworkProfile(Guid Id, string Name, List<FirewallRule> Rules, string NetworkId = "");
public sealed record PolicyDocument(int Version, Guid SelectedProfile, bool AutoSwitch, List<NetworkProfile> Profiles)
{
    public WindowsFirewallSyncState? WindowsSync { get; init; }
    public static PolicyDocument Empty() { var id = Guid.NewGuid(); return new(1, id, false, [new(id, "Default", [])]); }
    public void Validate()
    {
        if (Version != 1 || Profiles is null || Profiles.Count is < 1 or > 32 || Profiles.Any(x => x is null || x.Rules is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 80 || x.NetworkId is null || x.NetworkId.Length > 128)) throw new InvalidDataException("Invalid policy profile file.");
        if (Profiles.Select(x => x.Id).Distinct().Count() != Profiles.Count || Profiles.All(x => x.Id != SelectedProfile)) throw new InvalidDataException("Invalid profile selection.");
        var bindings = Profiles.Where(x => x.NetworkId.Length > 0).Select(x => x.NetworkId).ToList();
        if (bindings.Distinct().Count() != bindings.Count) throw new InvalidDataException("A network can be associated with only one profile.");
        foreach (var profile in Profiles)
        {
            if (profile.Rules.Count > 10000 || profile.Rules.Any(x => x is null) || profile.Rules.Select(x => x.Id).Distinct().Count() != profile.Rules.Count) throw new InvalidDataException("Too many rules, empty rules or duplicate rule IDs.");
            foreach (var rule in profile.Rules) { if (rule is null) throw new InvalidDataException("Empty rule."); rule.Validate(); }
        }
        WindowsSync?.Validate(this);
    }
}

public sealed record BrokerRequest(string Command, List<FirewallRule>? Rules = null, List<string>? RemoteAddresses = null, WindowsFirewallBatch? WindowsBatch = null);
public sealed record BrokerResponse(bool Success, string Message, int ActiveRules = 0, UsageSnapshot? Usage = null, List<CachedDomainAnswer>? Domains = null);
