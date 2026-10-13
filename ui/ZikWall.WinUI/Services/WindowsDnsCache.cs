using System.Management;
using System.Net;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Documented, local, read-only Windows DNS cache provider. No DNS queries,
// PowerShell processes, packet capture, or unsupported dnsapi exports.
internal sealed class WindowsDnsCache
{
    private bool accessDenied;
    internal const string Query = "SELECT Name, Data, TimeToLive FROM MSFT_DNSClientCache WHERE (Type=1 OR Type=28) AND Status=0 AND Section=1 AND Data<>'0.0.0.0' AND Data<>'::'";
    public IReadOnlyList<CachedDomainAnswer> Read(IReadOnlySet<string> wanted)
    {
        if (!OperatingSystem.IsWindows() || wanted.Count == 0) return [];
        if (accessDenied) throw new UnauthorizedAccessException("Windows DNS cache access requires elevation.");
        try { return ReadProvider(wanted); }
        catch (UnauthorizedAccessException) { accessDenied = true; throw; }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied) { accessDenied = true; throw; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<CachedDomainAnswer> ReadProvider(IReadOnlySet<string> wanted)
    {
        // Forward-only streaming bounds client-side memory. Provider enumeration
        // work is owned by Windows; its duration is not a hard real-time bound.
        var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, BlockSize = 64, Timeout = TimeSpan.FromSeconds(3) };
        using var search = new ManagementObjectSearcher(new ManagementScope(@"root\StandardCimv2"), new ObjectQuery(Query), options);
        using var records = search.Get();
        var result = new List<CachedDomainAnswer>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int seen = 0;
        foreach (ManagementObject record in records)
        {
            using (record)
            {
                if (++seen > 16384) break;
                if (Decode(record["Data"] as string, record["Name"] as string, Convert.ToUInt32(record["TimeToLive"]), wanted) is not { } answer) continue;
                counts.TryGetValue(answer.Address, out int count);
                if (count >= 3) continue;
                counts[answer.Address] = count + 1;
                result.Add(answer);
            }
        }
        return result;
    }

    internal static CachedDomainAnswer? Decode(string? address, string? name, uint ttl, IReadOnlySet<string> wanted)
    {
        if (ttl == 0 || !IPAddress.TryParse(address, out var ip) || name is null) return null;
        string canonical = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        // Use the address record's owner name, not an unverified CNAME alias.
        return wanted.Contains(canonical) && CachedDomainHints.ValidName(name) ? new(canonical, name, ttl) : null;
    }
}
