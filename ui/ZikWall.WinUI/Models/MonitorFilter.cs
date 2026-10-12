namespace ZikWall.WinUI.Models;

public static class MonitorFilter
{
    public static bool Matches(ConnectionEntry entry, string query, int scope, int direction) =>
        entry.Matches(query)
        && (scope == 0 || (scope == 1 ? entry.NetworkScope == "Internet" : entry.NetworkScope != "Internet"))
        && (direction == 0 || entry.Direction == (direction == 1 ? "Outgoing" : "Incoming"));
}
