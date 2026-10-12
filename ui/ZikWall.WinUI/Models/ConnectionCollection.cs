using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ZikWall.WinUI.Models;

// Keep unchanged native list items alive instead of rebinding the whole view.
// Heavy churn takes one reset; incremental work is capped to avoid quadratic moves.
public sealed class ConnectionCollection : ObservableCollection<ConnectionEntry>
{
    private const int MaximumChanges = 64;
    public void Update(IReadOnlyList<ConnectionEntry> desired)
    {
        var next = new Dictionary<ConnectionIdentity, ConnectionEntry>(desired.Count);
        foreach (var entry in desired)
            if (!next.TryAdd(entry.Identity, entry)) { ResetTo(desired); return; }
        int changes = Math.Abs(Count - desired.Count);
        foreach (var entry in this)
            if (!next.TryGetValue(entry.Identity, out var replacement) || entry != replacement) changes++;
        if (changes > MaximumChanges) { ResetTo(desired); return; }
        int operations = 0;
        for (int i = Count - 1; i >= 0; i--)
            if (!next.ContainsKey(this[i].Identity)) { RemoveAt(i); operations++; }
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Count && this[i].Identity == desired[i].Identity)
            {
                if (this[i] != desired[i]) { this[i] = desired[i]; operations++; }
            }
            else
            {
                int existing = -1;
                for (int j = i + 1; j < Count; j++)
                    if (this[j].Identity == desired[i].Identity) { existing = j; break; }
                if (existing >= 0)
                {
                    Move(existing, i);
                    if (this[i] != desired[i]) this[i] = desired[i];
                }
                else Insert(i, desired[i]);
                operations++;
            }
            if (operations > MaximumChanges) { ResetTo(desired); return; }
        }
    }
    private void ResetTo(IReadOnlyList<ConnectionEntry> desired)
    {
        Items.Clear();
        foreach (var entry in desired) Items.Add(entry);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
