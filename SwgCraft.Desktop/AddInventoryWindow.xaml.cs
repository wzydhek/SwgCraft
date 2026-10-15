using System.Windows;
using System.Windows.Controls;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

public sealed record PickRow(long SpawnId, string Name, string TypeName, string Stats, string Spawned,
                             string Status, string Source, bool IsManual);

/// <summary>Pick a spawn (name, type and stats are shown so reused names are easy to tell apart) and add a quantity of it.</summary>
public partial class AddInventoryWindow : Window
{
    readonly int _galaxyId;

    public AddInventoryWindow(int galaxyId)
    {
        _galaxyId = galaxyId;
        InitializeComponent();
        Loaded += (_, _) => { Refresh(); SearchBox.Focus(); };
    }

    IGalaxySpawnStore Store => Services.Spawns.ForGalaxy(_galaxyId);

    void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) Refresh(); }
    void Despawned_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Refresh(); }

    void Refresh(long? select = null)
    {
        var term = SearchBox.Text.Trim();
        var spawns = Store.Spawns(IncludeDespawned.IsChecked != true, null);
        var rows = spawns.Select(ToRow).Where(r => term.Length == 0
                      || r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                      || r.TypeName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        PickGrid.ItemsSource = rows;

        if (select == null) return;
        var row = rows.FirstOrDefault(r => r.SpawnId == select);
        PickGrid.SelectedItem = row;
        if (row != null) PickGrid.ScrollIntoView(row);
    }

    static PickRow ToRow(Spawn s)
    {
        var node = Services.Tree.Find(s.ResourceTypeId);
        bool manual = s.Source == SpawnSources.Manual;
        return new PickRow(
            s.Id, s.Name, node?.Name ?? $"{s.ResourceTypeId} (not in tree)",
            string.Join("  ", s.Stats.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")),
            SpawnDates.IsUnknown(s.SpawnedUtc) ? "unknown" : s.SpawnedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            s.DespawnedUtc == null ? "Current" : manual ? "Past spawn" : "Despawned " + s.DespawnedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd"),
            manual ? "Manual" : "Feed", manual);
    }

    // ------------------------------------------------------------ manual spawns

    void NewSpawn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ManualSpawnWindow(_galaxyId) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        SearchBox.Text = "";
        IncludeDespawned.IsChecked = true;      // manual spawns are past spawns, so they only show with this on
        Refresh(dlg.ResultSpawnId);
        Services.RaiseSpawnDataChanged();
        QtyBox.Focus();
    }

    void EditSpawn_Click(object sender, RoutedEventArgs e)
    {
        if (!TryManualSelection(out var row)) return;
        var spawn = Store.Spawns(false, null).FirstOrDefault(s => s.Id == row.SpawnId);
        if (spawn == null) return;

        var dlg = new ManualSpawnWindow(_galaxyId, spawn) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Refresh(dlg.ResultSpawnId);
        Services.RaiseSpawnDataChanged();
    }

    void DeleteSpawn_Click(object sender, RoutedEventArgs e)
    {
        if (!TryManualSelection(out var row)) return;
        if (MessageBox.Show(this, $"Delete the manual spawn '{row.Name}'?", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!Store.DeleteManualSpawn(row.SpawnId))
        {
            MessageBox.Show(this, $"'{row.Name}' is in your inventory. Remove that stack first, then delete the spawn.", Title);
            return;
        }
        Refresh();
        Services.RaiseSpawnDataChanged();
    }

    bool TryManualSelection(out PickRow row)
    {
        row = (PickRow)PickGrid.SelectedItem!;
        if (PickGrid.SelectedItem is PickRow { IsManual: true }) return true;
        MessageBox.Show(this,
            PickGrid.SelectedItem == null
                ? "Select a manually entered spawn first."
                : "Only manually entered spawns can be edited or deleted. Feed spawns are corrected by the next sync.",
            Title);
        return false;
    }

    // ------------------------------------------------------------ add to inventory

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (PickGrid.SelectedItem is not PickRow row)
        {
            MessageBox.Show(this, "Select a resource in the list.", Title);
            return;
        }
        if (!int.TryParse(QtyBox.Text.Trim(), out var qty) || qty <= 0)
        {
            MessageBox.Show(this, "Enter a quantity greater than zero.", Title);
            return;
        }
        var note = NoteBox.Text.Trim();
        var store = Store;
        var result = store.AdjustInventory(row.SpawnId, qty, note.Length == 0 ? null : note);

        // A result equal to the amount just added means this created the stack: fill in the default price per unit.
        // The price of an existing stack is never recalculated; edit it in the inventory window.
        if (result == qty)
        {
            var spawn = store.Spawns(false, null).FirstOrDefault(s => s.Id == row.SpawnId);
            if (spawn != null && UnitPricing.Calculator.Calculate(spawn, Services.Tree.Find(spawn.ResourceTypeId)) is double price)
                store.SetPrice(row.SpawnId, price);
        }
        DialogResult = true;
    }
}
