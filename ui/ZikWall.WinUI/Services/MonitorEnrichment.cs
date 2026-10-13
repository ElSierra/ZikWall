using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed class MonitorEnrichment : IDisposable
{
    private readonly CountryDatabase? countries;
    private readonly NetworkDatabase? networks;
    private CachedDomainHints domains = new();
    private readonly ConnectionContinuity continuity = new(TcpDirection.Read);
    public DomainCacheState DomainState => domains.State;
    public ConnectionEntry WithDomainHint(ConnectionEntry row) => row with { DomainHint = row.RemotePort is > 0 ? domains.Find(row.RemoteAddress) : "" };
    public void SetDomainReader(Func<IReadOnlySet<string>, IReadOnlyList<CachedDomainAnswer>>? reader)
    {
        var next = reader is null ? new CachedDomainHints() : new(reader, () => Environment.TickCount64);
        Interlocked.Exchange(ref domains, next).Dispose();
    }
    public string CountryDataStatus { get; } = "Offline country data ready";
    public string NetworkDataStatus { get; } = "Offline network owner data ready";
    public MonitorEnrichment(string directory, string? networkDirectory = null)
    {
        try { countries = new(directory); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { CountryDataStatus = "Country data unavailable; showing connections without countries"; }
        try { networks = new(networkDirectory ?? Path.Combine(directory, "..", "Networks")); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { NetworkDataStatus = "Network owner data unavailable; showing connections without owners"; }
    }
    public IReadOnlyList<ConnectionEntry> Enrich(IReadOnlyList<ConnectionEntry> rows, bool background = false)
    {
        domains.RequestRefresh(rows, background);
        return continuity.Apply(rows, Environment.TickCount64).Select(row =>
        {
            string scope = row.RemotePort is > 0 ? NetworkScope.Classify(row.RemoteAddress) : row.Protocol.StartsWith("UDP") ? "Local endpoint" : "Listening";
            var country = scope == "Internet" ? countries?.Find(row.RemoteAddress) ?? ("", "") : ("", "");
            var network = scope == "Internet" ? networks?.Find(row.RemoteAddress) ?? (0u, "") : (0u, "");
            return row with { NetworkScope = scope, CountryCode = country.Item1, CountryName = country.Item2, NetworkAsn = network.Item1, NetworkName = network.Item2, DomainHint = row.RemotePort is > 0 ? domains.Find(row.RemoteAddress) : "" };
        }).ToArray();
    }
    public void Dispose() { domains.Dispose(); countries?.Dispose(); networks?.Dispose(); }
}
