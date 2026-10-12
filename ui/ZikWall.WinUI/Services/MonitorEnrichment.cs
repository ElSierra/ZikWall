using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed class MonitorEnrichment : IDisposable
{
    private readonly CountryDatabase? countries;
    public string CountryDataStatus { get; } = "Offline country data ready";
    public MonitorEnrichment(string directory)
    {
        try { countries = new(directory); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { CountryDataStatus = "Country data unavailable; showing connections without countries"; }
    }
    public IReadOnlyList<ConnectionEntry> Enrich(IReadOnlyList<ConnectionEntry> rows) => rows.Select(row =>
    {
        string scope = row.RemotePort is > 0 ? NetworkScope.Classify(row.RemoteAddress) : row.Protocol.StartsWith("UDP") ? "Local endpoint" : "Listening";
        var country = scope == "Internet" ? countries?.Find(row.RemoteAddress) ?? ("", "") : ("", "");
        return row with { Direction = TcpDirection.Read(row), NetworkScope = scope, CountryCode = country.Item1, CountryName = country.Item2 };
    }).ToArray();
    public void Dispose() => countries?.Dispose();
}
