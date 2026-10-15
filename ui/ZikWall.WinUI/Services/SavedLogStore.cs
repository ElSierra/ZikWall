using System.Text.Json;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public sealed record LogCursor(DateTimeOffset Time, string Id) : IComparable<LogCursor>
{
    public int CompareTo(LogCursor? other) => other is null ? 1 : Time != other.Time
        ? Time.CompareTo(other.Time) : StringComparer.Ordinal.Compare(Id, other.Id);
    public static LogCursor For(ConnectionEvent value) => new(value.Time, LogReadState.EventId(value));
}

public sealed record SavedLogResult(LogAnalysisResult Timeline, LogCursor? Next, DateTimeOffset LatestTime);

// Queries the durable archive independently of the monitor's bounded recent cache.
// Keeps one first observation per app and one displayed page, not the entire history.
public sealed class SavedLogStore(string directory)
{
    private sealed record Batch(List<ConnectionEvent>? Events);

    public SavedLogResult Query(IReadOnlyList<ConnectionEvent> live, DateTimeOffset now, LogReadState readState,
        LogFilter filter = LogFilter.Important, LogRange range = LogRange.All, string query = "", bool unreadOnly = false,
        Func<ConnectionEntry, bool>? visible = null, Func<ConnectionEntry, ConnectionEntry>? enrich = null,
        LogCursor? before = null, CancellationToken cancellationToken = default)
    {
        string archive = Path.Combine(directory, "SavedHistory", "Activity");
        var files = Directory.Exists(archive) ? Directory.EnumerateFiles(archive, "*.jsonl")
            .Order(StringComparer.Ordinal).Select(path => (Path: path, Length: new FileInfo(path).Length)).ToArray() : [];

        IEnumerable<ConnectionEvent> Read()
        {
            var remaining = live.ToHashSet();
            foreach (var item in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bounded = new SnapshotStream(file, item.Length);
                using var reader = new StreamReader(bounded);
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Batch? batch;
                    try { batch = JsonSerializer.Deserialize<Batch>(line); }
                    catch (JsonException) { continue; } // Interrupted appends are preserved, as in ActivityStore recovery.
                    if (batch?.Events is null) continue;
                    foreach (var value in batch.Events)
                    {
                        if (value?.Connection is null) continue;
                        remaining.Remove(value);
                        yield return value;
                    }
                }
            }
            foreach (var value in live)
                if (remaining.Remove(value)) yield return value;
        }

        var firstApps = new Dictionary<string, ConnectionEvent>(StringComparer.Ordinal);
        DateTimeOffset latest = DateTimeOffset.MinValue;
        foreach (var value in Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Time > latest) latest = value.Time;
            if (value.Event != "First seen") continue;
            if (!firstApps.TryGetValue(value.Connection.AppKey, out var first) || value.Time < first.Time
                || value.Time == first.Time && LogCursor.For(value).CompareTo(LogCursor.For(first)) < 0)
                firstApps[value.Connection.AppKey] = value;
        }
        bool IsUnread(ConnectionEvent value) => value.Time > readState.ReadThrough && !readState.IsRead(value);
        int unread = firstApps.Values.Count(x => !x.IsTcpDetail && (visible?.Invoke(x.Connection) ?? true) && IsUnread(x));
        int matching = 0;
        query = query.Trim();
        var today = now.ToLocalTime().Date;
        var page = new PriorityQueue<LogEntry, LogCursor>();
        var pageIds = new HashSet<LogCursor>();
        var candidates = filter is LogFilter.Important or LogFilter.FirstApp ? firstApps.Values : Read();
        foreach (var value in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.IsTcpDetail || !(visible?.Invoke(value.Connection) ?? true)) continue;
            bool first = value.Event == "First seen" && firstApps[value.Connection.AppKey] == value;
            var category = first ? LogCategory.FirstApp : value.Event == "First seen" ? LogCategory.Connection
                : value.Event == "No longer observed" ? LogCategory.Closed : LogCategory.State;
            bool isUnread = first && IsUnread(value);
            if (unreadOnly && !isUnread) continue;
            if (filter == LogFilter.Important && !first || filter >= LogFilter.FirstApp
                && (int)category != (int)filter - (int)LogFilter.FirstApp) continue;
            if (range == LogRange.Today && value.Time.ToLocalTime().Date != today
                || range == LogRange.Day && value.Time < now.AddDays(-1)
                || range == LogRange.Week && value.Time < now.AddDays(-7)) continue;
            var row = new LogEntry(query.Length > 0 && enrich is not null
                ? value with { Connection = enrich(value.Connection) } : value, category, isUnread);
            if (query.Length > 0 && !row.Connection.Matches(query)
                && !row.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !value.Event.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            matching++;
            var position = LogCursor.For(value);
            if (before is not null && position.CompareTo(before) >= 0) continue;
            if (!pageIds.Add(position)) continue;
            page.Enqueue(row, position);
            if (page.Count > LogAnalysis.DisplayLimit + 1)
            {
                page.TryDequeue(out _, out var removed);
                pageIds.Remove(removed!);
            }
        }
        var rows = page.UnorderedItems.OrderByDescending(x => x.Priority).Take(LogAnalysis.DisplayLimit).ToArray();
        var groups = rows.Select(x => query.Length == 0 && enrich is not null
                ? x.Element with { Source = x.Element.Source with { Connection = enrich(x.Element.Connection) } } : x.Element)
            .GroupBy(x => x.Source.Time.ToLocalTime().Date)
            .Select(g => new LogDayGroup(g.Key == today ? "Today" : g.Key == today.AddDays(-1) ? "Yesterday" : g.Key.ToString("dddd, MMMM d, yyyy"), g)).ToList();
        return new(new(groups, matching, unread), page.Count > LogAnalysis.DisplayLimit ? rows[^1].Priority : null, latest);
    }

    // Both query passes see the same file lengths even while monitoring appends.
    private sealed class SnapshotStream(Stream source, long length) : Stream
    {
        private readonly long snapshotLength = length;
        private long remaining = length;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            remaining -= read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => snapshotLength;
        public override long Position { get => snapshotLength - remaining; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
