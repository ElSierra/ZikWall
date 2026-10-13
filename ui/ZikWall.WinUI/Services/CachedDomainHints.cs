using System.Net;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public enum DomainCacheState { Waiting, Reading, Ready, AccessDenied, Unavailable }

// Display hints only. Never used for connection identity or firewall rules.
// One worker, no timer of its own, no network requests and no waits on the UI path.
public sealed class CachedDomainHints : IDisposable
{
    internal const int MaxAddresses = 2048;
    private sealed record Hint(string Names, long Expires, bool Observed = false);
    private Dictionary<string, Hint> snapshot = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlySet<string>, IReadOnlyList<CachedDomainAnswer>> read;
    private readonly Func<long> clock;
    private readonly bool liveResponses;
    private readonly object gate = new();
    private long nextRefresh;
    private bool disposed;
    private volatile DomainCacheState state;
    public DomainCacheState State => state;
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public CachedDomainHints() : this(new WindowsDnsCache().Read, () => Environment.TickCount64) { }
    internal CachedDomainHints(Func<IReadOnlySet<string>, IReadOnlyList<CachedDomainAnswer>> read, Func<long> clock, bool liveResponses = false)
    { this.read = read; this.clock = clock; this.liveResponses = liveResponses; }

    public void RequestRefresh(IReadOnlyList<ConnectionEntry> rows, bool background)
    {
        lock (gate)
        {
            long now = clock();
            if (disposed || !Pending.IsCompleted || now < nextRefresh) return;
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.RemotePort is > 0 && IPAddress.TryParse(row.RemoteAddress, out var ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
                    addresses.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString());
                if (addresses.Count >= MaxAddresses) break;
            }
            if (addresses.Count == 0 && !liveResponses) { Volatile.Write(ref snapshot, new(StringComparer.Ordinal)); state = DomainCacheState.Waiting; return; }
            nextRefresh = now + (liveResponses ? (background ? 10000 : 2000) : (background ? 120000 : 60000));
            state = DomainCacheState.Reading;
            Pending = StartRefresh(addresses, now);
        }
    }

    private Task StartRefresh(HashSet<string> addresses, long now) => Task.Run(() =>
    {
        var next = new Dictionary<string, Hint>(StringComparer.Ordinal);
        var result = DomainCacheState.Ready;
        try
        {
            var answers = read(addresses);
            // A budgeted scan may cover only part of the OS cache. Keep
            // earlier hints until their original expiry, never extend it.
            foreach (var (address, hint) in Volatile.Read(ref snapshot))
                if (addresses.Contains(address) && hint.Expires > now && !(liveResponses && hint.Observed)) next[address] = hint;
            // Keep only a few names per IP. A shared CDN IP is ambiguous;
            // no app-specific domain claim can be made from the OS cache.
            var grouped = new Dictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
            var observed = answers.Take(8192).Where(x => x.ObservedResponse && x.TtlSeconds > 0 && ValidName(x.Name)).Select(x => x.Address).ToHashSet(StringComparer.Ordinal);
            int seen = 0;
            foreach (var answer in answers)
            {
                if (++seen > 8192) break;
                if (!addresses.Contains(answer.Address) || answer.TtlSeconds == 0 || !ValidName(answer.Name)) continue;
                if (observed.Contains(answer.Address) && !answer.ObservedResponse) continue;
                string name = answer.Name.TrimEnd('.').ToLowerInvariant();
                if (!grouped.TryGetValue(answer.Address, out var names)) grouped[answer.Address] = names = new(StringComparer.Ordinal);
                long expires = now + Math.Min(answer.TtlSeconds, answer.ObservedResponse ? 3600u : 120u) * 1000L;
                if (names.TryGetValue(name, out long previous)) names[name] = Math.Min(previous, expires);
                else if (names.Count < 3) names[name] = expires;
            }
            foreach (var (address, names) in grouped)
                next[address] = new(string.Join(", ", names.Keys), names.Values.Min(), observed.Contains(address));
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            result = ex is UnauthorizedAccessException || (OperatingSystem.IsWindows() && ex is System.Management.ManagementException { ErrorCode: System.Management.ManagementStatus.AccessDenied })
                ? DomainCacheState.AccessDenied : DomainCacheState.Unavailable;
        }
        lock (gate) { if (!disposed) { Volatile.Write(ref snapshot, next); state = result; } }
    });

    public string Find(string address) => Resolve(address).Name;

    internal (string Name, bool Observed) Resolve(string address)
    {
        var current = Volatile.Read(ref snapshot);
        if (current.Count == 0) return ("", false);
        // ConnectionMonitor already supplies canonical addresses. Only normalize
        // mapped IPv6 when needed; the common IPv4/IPv6 path allocates nothing.
        if (address.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(address, out var ip) && ip.IsIPv4MappedToIPv6)
            address = ip.MapToIPv4().ToString();
        return current.TryGetValue(address, out var hint) && clock() < hint.Expires ? (hint.Names, hint.Observed) : ("", false);
    }

    public bool IsObserved(string address)
    {
        return Resolve(address).Observed;
    }

    internal static bool ValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 254 || IPAddress.TryParse(name, out _)) return false;
        name = name.TrimEnd('.');
        if (!name.Contains('.')) return false;
        foreach (string label in name.Split('.'))
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        return true;
    }

    public void Dispose() { lock (gate) { disposed = true; Volatile.Write(ref snapshot, new(StringComparer.Ordinal)); } }
}
