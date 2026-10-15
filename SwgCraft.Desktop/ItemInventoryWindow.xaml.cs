using System.Windows;
using System.Windows.Controls;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

/// <summary>A row of the item inventory grid.</summary>
public sealed record ItemStackRow(long Id, MemberKind Kind, string ItemId, string TypeText, string Name,
                                  string? Serial, string SerialText, int Quantity, string Note, string Updated)
{
    /// <summary>The note on one line for the grid (line breaks shown as " / "); the full note is in the tooltip and the note box.</summary>
    public string NoteOneLine => Note.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " / ");
}

/// <summary>Resources > Item Inventory: looted and crafted items in the active galaxy, for build bills of materials.</summary>
public partial class ItemInventoryWindow : Window
{
    const string LootPrefix = "loot:";
    const string SchematicPrefix = "schematic:";

    readonly SuggestComboBox _itemBox;
    List<RefOption> _itemOptions = new();
    Dictionary<string, string> _names = new(StringComparer.Ordinal);   // picker key -> display name
    List<ItemStackRow> _rows = new();

    public ItemInventoryWindow()
    {
        InitializeComponent();
        _itemBox = new SuggestComboBox(_itemOptions) { AllowEmpty = true };
        ItemHost.Child = _itemBox;

        Services.ActiveGalaxyChanged += Refresh;
        Closed += (_, _) => Services.ActiveGalaxyChanged -= Refresh;
        Activated += (_, _) => RefreshItems();   // looted items or schematics may have been edited in another window
        RefreshItems(force: true);
    }

    IGalaxySpawnStore? Store =>
        Services.Settings.ActiveGalaxyId is int gid ? Services.Spawns.ForGalaxy(gid) : null;

    ItemStackRow? Selected => ItemGrid.SelectedItem as ItemStackRow;

    static string KeyFor(MemberKind kind, string id) => (kind == MemberKind.LootItem ? LootPrefix : SchematicPrefix) + id;

    static (MemberKind Kind, string Id)? ParseKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith(LootPrefix, StringComparison.Ordinal)) return (MemberKind.LootItem, key[LootPrefix.Length..]);
        if (key.StartsWith(SchematicPrefix, StringComparison.Ordinal)) return (MemberKind.Schematic, key[SchematicPrefix.Length..]);
        return null;
    }

    // ------------------------------------------------------------ item list (looted items + schematics)

    /// <summary>Rebuilds the picker from the static data on disk; the grid is redrawn only if a name changed.</summary>
    void RefreshItems(bool force = false)
    {
        var options = new List<RefOption>();
        foreach (var l in Services.StaticData.LoadLootItems().Value.Items)
            if (!string.IsNullOrEmpty(l.Id))
                options.Add(new RefOption(KeyFor(MemberKind.LootItem, l.Id), "Looted: " + (string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name.Trim())));
        foreach (var s in Services.StaticData.LoadSchematics().Value.Schematics)
            if (!string.IsNullOrEmpty(s.Id))
                options.Add(new RefOption(KeyFor(MemberKind.Schematic, s.Id), "Crafted: " + (string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name.Trim())));
        options.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));

        if (!force && options.SequenceEqual(_itemOptions)) return;
        _itemOptions = options;
        _itemBox.SetOptions(_itemOptions);
        _names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in options) _names.TryAdd(o.Key, o.Display[(o.Display.IndexOf(':') + 2)..]);   // name without "Looted: "/"Crafted: "
        Refresh();
    }

    // ------------------------------------------------------------ display

    void Refresh()
    {
        var store = Store;
        if (store == null)
        {
            ItemGrid.ItemsSource = null;
            CountText.Text = "";
            StatusText.Text = "No active galaxy selected.";
            return;
        }
        var keep = Selected?.Id;
        _refreshing = true;
        try
        {
            _rows = store.ItemInventory().Select(s =>
            {
                var key = KeyFor(s.Kind, s.ItemId);
                var name = _names.TryGetValue(key, out var n) ? n : $"{s.ItemId} (not in static data)";   // deleted or renamed ID stays visible
                return new ItemStackRow(s.Id, s.Kind, s.ItemId, s.Kind == MemberKind.LootItem ? "Looted" : "Crafted", name,
                                        s.Serial, s.Serial ?? "", s.Quantity, s.Note ?? "", s.UpdatedUtc.ToLocalTime().ToString("g"));
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SerialText, StringComparer.OrdinalIgnoreCase)
            .ToList();
            ApplySearch();
            if (keep != null) ItemGrid.SelectedItem = _rows.FirstOrDefault(r => r.Id == keep);
        }
        finally { _refreshing = false; }

        // the stack whose note is being edited is gone (used up or removed): clear the note box
        if (_noteStackId != null && _rows.All(r => r.Id != _noteStackId))
        {
            _noteStackId = null;
            _noteSaved = "";
            NoteBox.Text = "";
            NotePanel.IsEnabled = false;
            NotePanel.Header = "Note for the selected stack";
        }
        else if (_noteStackId != null && !NoteChanged && _rows.FirstOrDefault(r => r.Id == _noteStackId) is { } same)
        {
            _noteSaved = same.Note;   // pick up the stored text when nothing is being edited
            NoteBox.Text = same.Note;
        }
        if (Selected is { } sel) SerialBox.Text = sel.SerialText;
        CountText.Text = $"{_rows.Count:N0} stack(s), {_rows.Sum(r => (long)r.Quantity):N0} item(s)";
    }

    void ApplySearch()
    {
        var f = SearchBox.Text?.Trim() ?? "";
        ItemGrid.ItemsSource = f.Length == 0 ? _rows
            : _rows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                            || r.SerialText.Contains(f, StringComparison.OrdinalIgnoreCase)
                            || r.Note.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void Search_Changed(object sender, TextChangedEventArgs e) => ApplySearch();

    // ------------------------------------------------------------ note of the selected stack (multi-line)

    long? _noteStackId;          // the stack whose note is in the note box
    string _noteSaved = "";      // that note as stored, to tell whether the box has unsaved changes
    bool _refreshing;            // the grid is being rebuilt: selection changes are not the user's

    bool NoteChanged => _noteStackId != null && NormalizeNote(NoteBox.Text) != _noteSaved;

    static string NormalizeNote(string? text) => (text ?? "").Trim();

    void ItemGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        var row = Selected;
        SerialBox.Text = row?.SerialText ?? "";
        if (row != null && row.Id == _noteStackId) return;   // same stack (e.g. after a refresh): keep what is being typed

        OfferToSaveNote();
        _noteStackId = row?.Id;
        _noteSaved = row?.Note ?? "";
        NoteBox.Text = _noteSaved;
        NotePanel.IsEnabled = row != null;
        NotePanel.Header = row == null ? "Note for the selected stack" : $"Note for {row.Name}{(row.Serial != null ? $" (serial {row.Serial})" : "")}";
    }

    /// <summary>Before the note box switches to another stack (or the window closes), offers to save an edited note.</summary>
    void OfferToSaveNote()
    {
        if (!NoteChanged || Store is not { } store || _rows.FirstOrDefault(r => r.Id == _noteStackId) is not { } row) return;
        if (MessageBox.Show(this, $"Save the note you changed for {row.Name}?", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        store.UpdateItemStack(row.Id, row.Serial, row.Quantity, NoteBox.Text);
        StatusText.Text = $"{row.Name}: note saved.";
    }

    void NoteBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NotePanel.Header = (NotePanel.Header as string ?? "").TrimEnd(' ', '*') + (NoteChanged ? " *" : "");

    void UndoNote_Click(object sender, RoutedEventArgs e) => NoteBox.Text = _noteSaved;

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
            && NotePanel.IsEnabled)
        {
            SaveNote_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        OfferToSaveNote();
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ add

    void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store) { StatusText.Text = "No active galaxy selected."; return; }
        var key = _itemBox.ResolveKey();
        if (ParseKey(key) is not { } item) { StatusText.Text = "Choose an item first (type to search, then pick one)."; return; }
        if (!int.TryParse(NewQtyBox.Text.Trim(), out var qty) || qty < 1) { StatusText.Text = "Quantity must be a whole number of at least 1."; return; }

        var serial = NewSerialBox.Text.Trim();
        var total = store.AdjustItem(item.Kind, item.Id, serial, qty, NewNoteBox.Text);   // an empty note keeps the stack's old one
        var name = _names.TryGetValue(key!, out var n) ? n : item.Id;
        StatusText.Text = total == qty
            ? $"Added {qty} x {name}{(serial.Length > 0 ? $" (serial {serial})" : "")}."
            : $"Added {qty} x {name}{(serial.Length > 0 ? $" (serial {serial})" : "")}; that stack now has {total}.";
        NewSerialBox.Text = "";
        NewQtyBox.Text = "1";
        NewNoteBox.Text = "";
        Refresh();
    }

    // ------------------------------------------------------------ selected stack

    bool TryAmount(out int amount)
    {
        if (int.TryParse(AmountBox.Text.Trim(), out amount) && amount >= 0) return true;
        StatusText.Text = "Enter a whole number in the Amount box.";
        return false;
    }

    void Adjust(int sign)
    {
        if (Store is not { } store || Selected is not { } row) { StatusText.Text = "Select a stack first."; return; }
        if (!TryAmount(out var amount)) return;
        var result = store.AdjustItem(row.Kind, row.ItemId, row.Serial, sign * amount);
        StatusText.Text = result == null ? $"Only {row.Quantity} in that stack; nothing was changed."
                        : result == 0 ? $"{row.Name} stack used up and removed."
                        : $"{row.Name}: now {result}.";
        Refresh();
    }

    void Plus_Click(object sender, RoutedEventArgs e) => Adjust(1);
    void Minus_Click(object sender, RoutedEventArgs e) => Adjust(-1);

    void Set_Click(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store || Selected is not { } row) { StatusText.Text = "Select a stack first."; return; }
        if (!TryAmount(out var amount)) return;
        var r = store.UpdateItemStack(row.Id, row.Serial, amount, row.Note);
        StatusText.Text = r == ItemStackUpdate.Removed ? $"{row.Name} stack removed." : $"{row.Name}: set to {amount}.";
        Refresh();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store || Selected is not { } row) { StatusText.Text = "Select a stack first."; return; }
        if (MessageBox.Show(this, $"Remove the stack of {row.Quantity} x {row.Name}{(row.Serial != null ? $" (serial {row.Serial})" : "")}?",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        store.DeleteItemStack(row.Id);
        StatusText.Text = $"{row.Name} stack removed.";
        Refresh();
    }

    void SetSerial_Click(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store || Selected is not { } row) { StatusText.Text = "Select a stack first."; return; }
        var serial = SerialBox.Text.Trim();
        var r = store.UpdateItemStack(row.Id, serial, row.Quantity, row.Note);
        StatusText.Text = r == ItemStackUpdate.SerialTaken
            ? $"There is already a stack of {row.Name} with {(serial.Length == 0 ? "no serial" : $"serial {serial}")}. Add to that stack instead."
            : $"{row.Name}: serial {(serial.Length == 0 ? "cleared" : $"set to {serial}")}.";
        Refresh();
    }

    void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store || Selected is not { } row) { StatusText.Text = "Select a stack first."; return; }
        store.UpdateItemStack(row.Id, row.Serial, row.Quantity, NoteBox.Text);   // stored trimmed; line breaks inside are kept
        _noteSaved = NormalizeNote(NoteBox.Text);
        NoteBox.Text = _noteSaved;
        StatusText.Text = $"{row.Name}: note saved.";
        Refresh();
    }
}
