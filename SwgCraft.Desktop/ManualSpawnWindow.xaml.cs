using System.Windows;
using System.Windows.Controls;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

/// <summary>Adds, or edits, a past spawn that is not in the feed data.</summary>
public partial class ManualSpawnWindow : Window
{
    sealed record TypeItem(ResourceNode Node, string Display);

    readonly int _galaxyId;
    readonly Spawn? _editing;
    readonly List<TypeItem> _all;
    readonly Dictionary<Stat, string> _entered = new();
    readonly Dictionary<Stat, TextBox> _boxes = new();

    public long ResultSpawnId { get; private set; }

    public ManualSpawnWindow(int galaxyId, Spawn? editing = null)
    {
        _galaxyId = galaxyId;
        _editing = editing;
        InitializeComponent();

        _all = BuildTypeItems();
        TypeList.ItemsSource = _all;

        if (editing != null)
        {
            Title = "Edit Manual Spawn";
            NameBox.Text = editing.Name;
            foreach (var (stat, value) in editing.Stats) _entered[stat] = value.ToString();
            SpawnedPicker.SelectedDate = SpawnDates.IsUnknown(editing.SpawnedUtc) ? null : editing.SpawnedUtc.ToLocalTime().Date;
            DespawnedPicker.SelectedDate = editing.DespawnedUtc?.ToLocalTime().Date;
            TypeList.SelectedItem = _all.FirstOrDefault(t => t.Node.Id == editing.ResourceTypeId);
        }
        else ShowStats(null);

        Loaded += (_, _) => { if (editing == null) NameBox.Focus(); };
    }

    /// <summary>Spawnable types, each shown by its own name only ("Ferrous Metal"); a name shared by several types gets a short [parent] tag.</summary>
    static List<TypeItem> BuildTypeItems()
    {
        var labels = ResourceLabels.Build(Services.Tree.Document);
        var items = new List<TypeItem>();
        void Walk(ResourceNode n)
        {
            if (n.IsLeaf)
            {
                if (labels.TryGetValue(n.Id, out var label)) items.Add(new TypeItem(n, label));
            }
            else
                foreach (var c in n.Children!) Walk(c);
        }
        foreach (var root in Services.Tree.Document.Nodes) Walk(root);
        return items;
    }

    void TypeSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var selected = TypeList.SelectedItem as TypeItem;
        var term = TypeSearch.Text.Trim();
        var view = _all.Where(t => term.Length == 0 || t.Display.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        TypeList.ItemsSource = view;
        if (selected != null && view.Contains(selected)) TypeList.SelectedItem = selected;
    }

    void TypeList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowStats((TypeList.SelectedItem as TypeItem)?.Node);

    void ShowStats(ResourceNode? node)
    {
        foreach (var (stat, box) in _boxes) _entered[stat] = box.Text;   // keep what was typed if the type changes
        StatPanel.Children.Clear();
        _boxes.Clear();

        if (node == null) { StatHint.Text = "Choose a resource type to see which stats it has."; return; }

        bool hasRanges = node.Ranges is { Count: > 0 };
        StatHint.Text = hasRanges
            ? "Only the stats this resource type has are shown. Leave a stat blank if the resource doesn't have it."
            : "This type has no stat ranges defined yet, so all stats are shown.";

        var stats = hasRanges ? node.Ranges!.Keys.ToList() : StatInfo.All.ToList();
        foreach (var stat in stats)
        {
            var hint = node.Ranges != null && node.Ranges.TryGetValue(stat, out var r) ? $"  ({r.Min}-{r.Max})" : "";
            var box = new TextBox { Width = 70, Text = _entered.GetValueOrDefault(stat, "") };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = $"{stat} - {StatInfo.FullName(stat)}{hint}",
                Width = 270,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(box);
            StatPanel.Children.Add(row);
            _boxes[stat] = box;
        }
    }

    static DateTimeOffset? ToDate(DatePicker p) =>
        p.SelectedDate is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local)) : null;

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var stats = new Dictionary<Stat, int>();
        foreach (var (stat, box) in _boxes)
        {
            var text = box.Text.Trim();
            if (text.Length == 0) continue;
            if (!int.TryParse(text, out var value))
            {
                MessageBox.Show(this, $"{stat} must be a whole number.", Title);
                return;
            }
            stats[stat] = value;
        }

        var node = (TypeList.SelectedItem as TypeItem)?.Node;
        var input = new ManualSpawnInput(NameBox.Text, node?.Id ?? "", stats, ToDate(SpawnedPicker), ToDate(DespawnedPicker));
        var store = Services.Spawns.ForGalaxy(_galaxyId);

        var check = store.CheckManualSpawn(input, Services.Tree, _editing?.Id);
        if (check.Errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, check.Errors), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (check.Warnings.Count > 0 &&
            MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, check.Warnings) + "\n\nSave anyway?",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (_editing == null) ResultSpawnId = store.AddManualSpawn(input);
        else if (store.UpdateManualSpawn(_editing.Id, input)) ResultSpawnId = _editing.Id;
        else
        {
            MessageBox.Show(this, "That spawn can no longer be edited (only manual entries can).", Title);
            return;
        }
        DialogResult = true;
    }
}
