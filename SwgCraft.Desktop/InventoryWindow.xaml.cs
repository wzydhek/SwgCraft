using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

public sealed record StatCell(string Text, Brush Background);

public sealed record InventoryRow(
    long SpawnId, string Name, string TypeName, int Quantity,
    Dictionary<string, StatCell> Stats,
    string Price, double? PriceValue, bool Filtered, bool Generic,
    string FilterText, string GenericText, string Status, string Note);

public partial class InventoryWindow : Window
{
    static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    static readonly Brush HighBrush = Make(0xC8, 0xE6, 0xC9);   // light green: 90% of max or more
    static readonly Brush TopBrush = Make(0x81, 0xC7, 0x84);    // darker green: 95% of max or more

    public InventoryWindow()
    {
        InitializeComponent();
        AddStatColumns();
        Services.ActiveGalaxyChanged += Refresh;
        Services.SpawnDataChanged += Refresh;
        Services.TreeChanged += Refresh;
        Closed += (_, _) =>
        {
            Services.ActiveGalaxyChanged -= Refresh;
            Services.SpawnDataChanged -= Refresh;
            Services.TreeChanged -= Refresh;
        };
        Refresh();
    }

    void AddStatColumns()
    {
        var baseStyle = TryFindResource(typeof(DataGridCell)) as Style;   // keeps the normal selection look
        int at = 3;
        foreach (var stat in StatInfo.All)
        {
            var style = new Style(typeof(DataGridCell), baseStyle);
            style.Setters.Add(new Setter(Control.BackgroundProperty,
                new Binding($"Stats[{stat}].Background") { Mode = BindingMode.OneWay }));
            InvGrid.Columns.Insert(at++, new DataGridTextColumn
            {
                Header = stat.ToString(),
                Width = 44,
                Binding = new Binding($"Stats[{stat}].Text") { Mode = BindingMode.OneWay },
                CellStyle = style,
            });
        }
    }

    IGalaxySpawnStore? Store =>
        Services.Settings.ActiveGalaxyId is int gid ? Services.Spawns.ForGalaxy(gid) : null;

    InventoryRow? Selected => InvGrid.SelectedItem as InventoryRow;

    // ------------------------------------------------------------ display

    void Refresh()
    {
        var store = Store;
        if (store == null)
        {
            InvGrid.ItemsSource = null;
            StatusText.Text = "No active galaxy selected.";
            return;
        }

        var keep = Selected?.SpawnId;
        var filter = SearchBox.Text.Trim();
        var all = store.Inventory().Select(ToRow).ToList();
        var rows = all.Where(r => filter.Length == 0
                                  || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                  || r.TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        InvGrid.ItemsSource = rows;
        InvGrid.SelectedItem = keep == null ? null : rows.FirstOrDefault(r => r.SpawnId == keep);

        var priced = rows.Where(r => r.PriceValue != null).ToList();
        double value = priced.Sum(r => r.Quantity * r.PriceValue!.Value);
        StatusText.Text =
            $"Galaxy {store.GalaxyId}: {rows.Count} of {all.Count} resource stack(s), {rows.Sum(r => (long)r.Quantity):N0} units shown. " +
            $"Estimated value {value:N2}" + (priced.Count < rows.Count ? $" ({rows.Count - priced.Count} stack(s) without a price)." : ".");
    }

    static InventoryRow ToRow(InventoryItem i)
    {
        var s = i.Spawn;
        var node = Services.Tree.Find(s.ResourceTypeId);
        var pricing = Services.Settings.Pricing;

        var stats = new Dictionary<string, StatCell>();
        foreach (var stat in StatInfo.All)
        {
            if (!s.Stats.TryGetValue(stat, out var v)) { stats[stat.ToString()] = new StatCell("", Brushes.Transparent); continue; }
            int max = node?.Ranges != null && node.Ranges.TryGetValue(stat, out var range) ? range.Max : 0;
            int tier = StatQuality.Tier(v, max, pricing.HighThreshold, pricing.TopThreshold);
            stats[stat.ToString()] = new StatCell(v.ToString(), tier == 2 ? TopBrush : tier == 1 ? HighBrush : Brushes.Transparent);
        }

        return new InventoryRow(
            s.Id, s.Name, node?.Name ?? $"{s.ResourceTypeId} (not in tree)", i.Quantity, stats,
            i.PricePerUnit?.ToString("N1", CultureInfo.CurrentCulture) ?? "", i.PricePerUnit, s.Filtered, s.Generic,
            s.Filtered ? "Yes" : "", s.Generic ? "Yes" : "",
            s.DespawnedUtc == null ? "Current"
                : s.Source == SpawnSources.Manual ? "Past spawn"
                : "Despawned " + s.DespawnedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd"),
            i.Note ?? "");
    }

    void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) Refresh(); }

    void InvGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Selected;
        NoteBox.Text = row?.Note ?? "";
        PriceBox.Text = row?.PriceValue?.ToString(CultureInfo.CurrentCulture) ?? "";
        FilterCheck.IsChecked = row?.Filtered ?? false;
        GenericCheck.IsChecked = row?.Generic ?? false;
    }

    // ------------------------------------------------------------ actions

    void AddResource_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Settings.ActiveGalaxyId is not int gid)
        {
            MessageBox.Show(this, "Select a galaxy first.", Title);
            return;
        }
        if (new AddInventoryWindow(gid) { Owner = this }.ShowDialog() == true) Refresh();
    }

    bool TryAmount(bool allowZero, out int amount)
    {
        if (int.TryParse(AmountBox.Text.Trim(), out amount) && (allowZero ? amount >= 0 : amount > 0)) return true;
        StatusText.Text = allowZero ? "Enter an amount (0 or more) first." : "Enter a positive amount first.";
        return false;
    }

    bool TryTarget(out IGalaxySpawnStore store, out InventoryRow row)
    {
        store = Store!;
        row = Selected!;
        if (Store != null && Selected != null) return true;
        StatusText.Text = "Select a resource stack first.";
        return false;
    }

    void Plus_Click(object sender, RoutedEventArgs e) => Apply(+1);
    void Minus_Click(object sender, RoutedEventArgs e) => Apply(-1);

    void Apply(int sign)
    {
        if (!TryTarget(out var store, out var row) || !TryAmount(false, out var amount)) return;
        if (store.AdjustInventory(row.SpawnId, sign * amount) == null)
        {
            StatusText.Text = $"Cannot use {amount}: only {row.Quantity:N0} of {row.Name} on hand.";
            return;
        }
        AmountBox.Clear();
        Refresh();
    }

    void Set_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(out var store, out var row) || !TryAmount(true, out var amount)) return;
        store.SetInventory(row.SpawnId, amount, row.Note);
        AmountBox.Clear();
        Refresh();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(out var store, out var row)) return;
        if (MessageBox.Show(this, $"Remove {row.Quantity:N0} of '{row.Name}' from inventory?", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        store.SetInventory(row.SpawnId, 0, null);
        Refresh();
    }

    void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(out var store, out var row)) return;
        store.SetInventory(row.SpawnId, row.Quantity, NoteBox.Text);   // an empty box clears the note
        Refresh();
    }

    void SetPrice_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(out var store, out var row)) return;
        double? price = null;
        var text = PriceBox.Text.Trim();
        if (text.Length > 0)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var p)
                || p < 0 || double.IsNaN(p) || double.IsInfinity(p))
            {
                StatusText.Text = "Enter a price of 0 or more, or leave the box empty to clear it.";
                return;
            }
            price = CpuPrice.RoundPrice(p, Services.Settings.Pricing);   // typed prices follow the same rounding
        }
        store.SetPrice(row.SpawnId, price);
        Refresh();
    }

    void Recalc_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(out var store, out var row)) return;
        var spawn = store.Inventory().FirstOrDefault(i => i.Spawn.Id == row.SpawnId)?.Spawn;
        if (spawn == null) return;

        var price = UnitPricing.Calculator.Calculate(spawn, Services.Tree.Find(spawn.ResourceTypeId));
        if (price == null)
        {
            StatusText.Text = "No price could be calculated: check that the resource type has stat maximums in the tree, " +
                              "and that the pricing settings are valid.";
            return;
        }
        store.SetPrice(row.SpawnId, price);
        Refresh();
    }

    void FilterCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(out var store, out var row)) { FilterCheck.IsChecked = false; return; }
        store.SetFiltered(row.SpawnId, FilterCheck.IsChecked == true);
        Refresh();
        Services.RaiseSpawnDataChanged();
    }

    void GenericCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(out var store, out var row)) { GenericCheck.IsChecked = false; return; }
        store.SetGeneric(row.SpawnId, GenericCheck.IsChecked == true);
        Refresh();
        Services.RaiseSpawnDataChanged();
    }
}
