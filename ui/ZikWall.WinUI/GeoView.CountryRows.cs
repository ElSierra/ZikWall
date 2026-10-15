using System.ComponentModel;

namespace ZikWall.WinUI;

// Keep native list containers, flag images and progress bars alive across samples.
public sealed class CountryDisplayRow(CountryRow value) : INotifyPropertyChanged
{
    public CountryRow Value { get; private set; } = value;
    public string Code => Value.Code;
    public string Name => Value.Name;
    public string Flag => Value.Flag;
    public string IpsLabel => Value.IpsLabel;
    public string AppsLabel => Value.AppsLabel;
    public string Label => Value.Label;
    public double Share => Value.Share;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(CountryRow next)
    {
        var old = Value;
        if (old == next) return;
        Value = next;
        void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (old.Name != next.Name) Changed(nameof(Name));
        if (old.Flag != next.Flag) Changed(nameof(Flag));
        if (old.Ips != next.Ips) Changed(nameof(IpsLabel));
        if (old.Apps != next.Apps) Changed(nameof(AppsLabel));
        if (old.Share != next.Share) Changed(nameof(Share));
        Changed(nameof(Label));
    }
}

public sealed partial class GeoView
{
    private readonly System.Collections.ObjectModel.ObservableCollection<CountryDisplayRow> displayedCountries = [];

    private void UpdateCountryRows(List<CountryRow> rows)
    {
        var wanted = rows.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        for (int i = displayedCountries.Count - 1; i >= 0; i--)
            if (!wanted.Contains(displayedCountries[i].Code)) displayedCountries.RemoveAt(i);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = displayedCountries.FirstOrDefault(x => x.Code == rows[i].Code);
            if (row is null) displayedCountries.Insert(i, new(rows[i]));
            else
            {
                row.Update(rows[i]);
                int previousIndex = displayedCountries.IndexOf(row);
                if (previousIndex != i) displayedCountries.Move(previousIndex, i);
            }
        }
    }
}
