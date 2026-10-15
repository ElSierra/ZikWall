using System.ComponentModel;
using System.Collections.ObjectModel;

namespace ZikWall.WinUI;

public sealed class MapAppDisplayRow(MapAppRow value) : INotifyPropertyChanged
{
    public MapAppRow Value { get; private set; } = value;
    public string Key => Value.Key;
    public string Name => Value.Name;
    public string Path => Value.Path;
    public string Detail => Value.Detail;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(MapAppRow next)
    {
        var old = Value;
        if (old == next) return;
        Value = next;
        if (old.Name != next.Name) PropertyChanged?.Invoke(this, new(nameof(Name)));
        if (old.Path != next.Path) PropertyChanged?.Invoke(this, new(nameof(Path)));
        if (old.Ips != next.Ips) PropertyChanged?.Invoke(this, new(nameof(Detail)));
    }
}

public sealed partial class GeoView
{
    private readonly ObservableCollection<MapAppDisplayRow> displayedApps = [];
    private void UpdateAppRows(List<MapAppRow> rows)
    {
        var wanted = rows.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        for (int i = displayedApps.Count - 1; i >= 0; i--)
            if (!wanted.Contains(displayedApps[i].Key)) displayedApps.RemoveAt(i);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = displayedApps.FirstOrDefault(x => x.Key == rows[i].Key);
            if (row is null) displayedApps.Insert(i, new(rows[i]));
            else
            {
                row.Update(rows[i]);
                int previous = displayedApps.IndexOf(row);
                if (previous != i) displayedApps.Move(previous, i);
            }
        }
    }
}
