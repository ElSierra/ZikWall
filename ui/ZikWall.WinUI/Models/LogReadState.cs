using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZikWall.WinUI.Models;

public sealed record LogReadState(DateTimeOffset ReadThrough, HashSet<string> ReadEventIds)
{
    public static LogReadState Empty() => new(DateTimeOffset.MinValue, []);

    // Enrichment, display names and culture can change between rendering and restart.
    // Identify the original observation using only its persisted identity fields.
    public static string EventId(ConnectionEvent source) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object?[] { source.Time.UtcTicks,
            source.Event, source.Connection.Protocol, source.Connection.ProcessId,
            source.Connection.ProcessStarted, source.Connection.AppKey, source.Connection.LocalAddress,
            source.Connection.LocalPort, source.Connection.RemoteAddress, source.Connection.RemotePort }))));

    public bool IsRead(ConnectionEvent source) => source.Time <= ReadThrough || ReadEventIds.Contains(EventId(source));
    public void MarkRead(ConnectionEvent source) { if (source.Time > ReadThrough) ReadEventIds.Add(EventId(source)); }
}
