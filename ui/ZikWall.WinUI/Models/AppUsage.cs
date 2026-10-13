namespace ZikWall.WinUI.Models;

public sealed record AppTraffic(string Path, string PackageFamily, ulong Received, ulong Sent)
{
    [System.Text.Json.Serialization.JsonIgnore] public string AppKey => PackageFamily.Length > 0 ? "package:" + PackageFamily.ToUpperInvariant() : "path:" + Path.ToUpperInvariant();
}
public sealed record UsageSnapshot(Guid Session, DateTimeOffset Time, List<AppTraffic> Apps, ulong Events, ulong UnattributedBytes, ulong RejectedEvents, uint LostEvents, uint BufferKiB);
public sealed record DailyUsage(DateOnly Day, string AppKey, string Path, ulong Received, ulong Sent);
public sealed record AppUsageDisplay(decimal? Bytes, string Total, string Rate, string Hint)
{
    public static AppUsageDisplay Unknown { get; } = new(null, "—", "", "No usage recorded for this app today. Totals cover collection time only.");
}

// Cumulative collector counters make repeated reads idempotent. A new session starts
// at zero; restarting the UI never replays the previous session into today's total.
public sealed class UsageLedger
{
    private Guid session;
    private DateTimeOffset? previousTime;
    private Dictionary<string, AppTraffic> previous = new();
    private Dictionary<(DateOnly Day, string AppKey), DailyUsage> dailyIndex = new();
    public List<DailyUsage> Days { get; private set; } = [];
    public Dictionary<string, (double Down, double Up)> Rates { get; } = new();
    public void Load(IEnumerable<DailyUsage> days, DateOnly today)
    {
        var input = days.Take(15361).ToList();
        if (input.Count > 15360 || input.Any(x => x is null || string.IsNullOrEmpty(x.AppKey) || x.AppKey.Length > 1024 || x.Path is null || x.Path.Length > 32768)) throw new InvalidDataException("Invalid daily usage file.");
        Days = input.Where(x => x.Day <= today && x.Day >= today.AddDays(-29)).Take(15360).ToList();
        if (Days.Select(x => (x.Day, x.AppKey)).Distinct().Count() != Days.Count) throw new InvalidDataException("Duplicate daily usage rows.");
        dailyIndex = Days.ToDictionary(x => (x.Day, x.AppKey));
    }
    public void Observe(UsageSnapshot snapshot)
    {
        if (snapshot.Session == Guid.Empty || snapshot.Apps.Count > 512) throw new InvalidDataException("Invalid usage snapshot.");
        if (snapshot.Session == session && previousTime is { } time && snapshot.Time <= time) return;
        bool same = session == snapshot.Session;
        double seconds = same && previousTime.HasValue ? (snapshot.Time - previousTime.Value).TotalSeconds : 0;
        var today = DateOnly.FromDateTime(snapshot.Time.LocalDateTime);
        var days = Days.Where(x => x.Day >= today.AddDays(-29) && x.Day <= today).ToDictionary(x => (x.Day, x.AppKey));
        Rates.Clear();
        var apps = snapshot.Apps.GroupBy(x => x.AppKey).Select(group => group.First() with
        {
            Received = group.Aggregate(0UL, (total, row) => checked(total + row.Received)),
            Sent = group.Aggregate(0UL, (total, row) => checked(total + row.Sent))
        }).ToList();
        foreach (var app in apps)
        {
            previous.TryGetValue(app.AppKey, out var old);
            ulong down = same && old is not null ? app.Received >= old.Received ? app.Received - old.Received : 0 : app.Received;
            ulong up = same && old is not null ? app.Sent >= old.Sent ? app.Sent - old.Sent : 0 : app.Sent;
            Rates[app.AppKey] = (seconds > 0 ? down / seconds : 0, seconds > 0 ? up / seconds : 0);
            var key = (today, app.AppKey);
            var day = days.GetValueOrDefault(key) ?? new(today, app.AppKey, app.Path, 0, 0);
            days[key] = day with { Received = checked(day.Received + down), Sent = checked(day.Sent + up) };
        }
        Days = days.Values.OrderByDescending(x => x.Day).Take(15360).ToList();
        dailyIndex = days.Count <= 15360 ? days : Days.ToDictionary(x => (x.Day, x.AppKey));
        previous = apps.ToDictionary(x => x.AppKey);
        previousTime = snapshot.Time; session = snapshot.Session;
    }
    public static string Bytes(decimal value) => value >= 1073741824 ? $"{value / 1073741824m:0.00} GB" : value >= 1048576 ? $"{value / 1048576m:0.0} MB" : value >= 1024 ? $"{value / 1024m:0.0} KB" : $"{value} B";
    public (string Rate, string Total) Labels(string key, bool active, DateOnly today)
    {
        var day = dailyIndex.GetValueOrDefault((today, key));
        var rate = Rates.GetValueOrDefault(key);
        return (active ? Rates.ContainsKey(key) ? $"↓ {TrafficSample.Rate(rate.Down)} ↑ {TrafficSample.Rate(rate.Up)}" : "—" : "Usage paused",
            day is null ? "No usage recorded" : $"↓ {Bytes(day.Received)} ↑ {Bytes(day.Sent)}");
    }
    public AppUsageDisplay Display(string key, DateOnly today)
    {
        if (!dailyIndex.TryGetValue((today, key), out var day)) return AppUsageDisplay.Unknown;
        decimal bytes = (decimal)day.Received + day.Sent;
        string rate = Rates.TryGetValue(key, out var sample) ? $"↓ {TrafficSample.Rate(sample.Down)} ↑ {TrafficSample.Rate(sample.Up)}" : "";
        return new(bytes, Bytes(bytes), rate, $"Today: ↓ {Bytes(day.Received)} downloaded · ↑ {Bytes(day.Sent)} uploaded. Whole-app usage, including local traffic; totals cover collection time only.");
    }
}
