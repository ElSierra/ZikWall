using System.Net;
using System.Net.Sockets;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Optional network lookup, separate from passive Windows cache access. One
// worker and eight public IPs per minute bound resolver traffic and memory.
public sealed class ReverseDnsHints : IDisposable
{
    private sealed record Hint(string Name, long Expires);
    private readonly Dictionary<string, Hint> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Func<string, CancellationToken, Task<string>> lookup;
    private readonly Func<long> clock;
    private CancellationTokenSource lifetime = new();
    private bool enabled, disposed;
    private long nextRefresh;
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public ReverseDnsHints() : this(async (address, token) =>
        (await Dns.GetHostEntryAsync(address, AddressFamily.Unspecified, token)).HostName,
        () => Environment.TickCount64) { }
    internal ReverseDnsHints(Func<string, CancellationToken, Task<string>> lookup, Func<long> clock)
    { this.lookup = lookup; this.clock = clock; }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (disposed || enabled == value) return;
            enabled = value; nextRefresh = 0;
            if (!value) { lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); cache.Clear(); }
        }
    }
    public string Find(string address)
    {
        lock (gate) return enabled && cache.TryGetValue(address, out var hint) && hint.Expires > clock() ? hint.Name : "";
    }
    public void RequestRefresh(IReadOnlyList<ConnectionEntry> rows, bool background)
    {
        lock (gate)
        {
            long now = clock();
            if (!enabled || disposed || !Pending.IsCompleted || now < nextRefresh) return;
            foreach (var address in cache.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) cache.Remove(address);
            var addresses = rows.Where(x => x.RemotePort > 0 && x.DomainHint.Length == 0 && NetworkScope.Classify(x.RemoteAddress) == "Internet")
                .Select(x => x.RemoteAddress).Distinct(StringComparer.Ordinal).Where(x => !cache.ContainsKey(x)).Take(8).ToArray();
            if (addresses.Length == 0) return;
            nextRefresh = now + (background ? 120000 : 60000);
            var token = lifetime.Token;
            Pending = Task.Run(async () =>
            {
                foreach (string address in addresses)
                {
                    if (token.IsCancellationRequested) break;
                    string name = "";
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        string answer = await lookup(address, timeout.Token).WaitAsync(timeout.Token);
                        if (CachedDomainHints.ValidName(answer)) name = answer.TrimEnd('.').ToLowerInvariant();
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { }
                    lock (gate)
                    {
                        if (disposed || !enabled || token.IsCancellationRequested) break;
                        if (cache.Count >= 2048) cache.Remove(cache.MinBy(x => x.Value.Expires).Key);
                        cache[address] = new(name, clock() + (name.Length > 0 ? 600000 : 120000));
                    }
                }
            });
        }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; enabled = false; lifetime.Cancel(); lifetime.Dispose(); cache.Clear(); }
    }
}
