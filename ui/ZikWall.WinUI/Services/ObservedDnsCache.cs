using System.Net;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Session-owned, TTL-limited cache. Never stores packet payloads or changes rule identity.
internal sealed class ObservedDnsCache(Func<long>? clock = null)
{
    internal const int Capacity = 8192;
    private sealed record Entry(string Address, string Name, long Expires, long Seen);
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private readonly Dictionary<(string Address, string Name), LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> recency = [];
    // One expiration index entry per live pair, including after repeated replies.
    // Unlike a queue of deadlines, updates cannot accumulate stale records.
    private readonly SortedSet<Entry> expirations = new(Comparer<Entry>.Create((a, b) =>
    {
        int order = a.Expires.CompareTo(b.Expires);
        if (order == 0) order = StringComparer.Ordinal.Compare(a.Address, b.Address);
        return order == 0 ? StringComparer.Ordinal.Compare(a.Name, b.Name) : order;
    }));
    private readonly object gate = new();
    public void Observe(IEnumerable<CachedDomainAnswer> answers)
    {
        lock (gate)
        {
            long time = now();
            Expire(time);
            foreach (var answer in answers.Take(128))
            {
                if (!CachedDomainHints.ValidName(answer.Name) || !IPAddress.TryParse(answer.Address, out var ip)) continue;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(ip) || ip.IsIPv6Multicast || (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224)) continue;
                string address = ip.ToString();
                string name = answer.Name.TrimEnd('.').ToLowerInvariant();
                var key = (address, name);
                entries.TryGetValue(key, out var existing);
                if (answer.TtlSeconds == 0) { if (existing is not null) Remove(existing); continue; }
                if (existing is not null)
                {
                    expirations.Remove(existing.Value);
                    recency.Remove(existing);
                }
                else if (entries.Count >= Capacity) Remove(recency.First!);
                var value = new Entry(address, name, time + Math.Min(answer.TtlSeconds, 3600u) * 1000L, time);
                var node = existing ?? new LinkedListNode<Entry>(value);
                node.Value = value;
                recency.AddLast(node); expirations.Add(value); entries[key] = node;
            }
        }
    }
    public List<CachedDomainAnswer> Read(IReadOnlySet<string> wanted)
    {
        lock (gate)
        {
            long time = now();
            Expire(time);
            return entries.Values.Select(x => x.Value).Where(x => wanted.Contains(x.Address)).OrderByDescending(x => x.Seen)
                .GroupBy(x => x.Address).SelectMany(group => group.Take(3))
                .Select(x => new CachedDomainAnswer(x.Address, x.Name, (uint)Math.Max(1, (x.Expires - time) / 1000), true)).ToList();
        }
    }
    private void Expire(long time)
    {
        while (expirations.Min is { } expired && expired.Expires <= time)
            Remove(entries[(expired.Address, expired.Name)]);
    }
    private void Remove(LinkedListNode<Entry> node)
    {
        entries.Remove((node.Value.Address, node.Value.Name));
        expirations.Remove(node.Value);
        recency.Remove(node);
    }
}
