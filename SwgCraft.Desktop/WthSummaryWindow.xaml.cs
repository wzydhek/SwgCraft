using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

/// <summary>A spawn in the WTH Summary.</summary>
public sealed record WthSummaryRow(string Name, string TypeName, string Planets, Dictionary<string, StatCell> Stats, string Days, int DaysSort);

/// <summary>
/// Resources > WTH Summary: the spawns that are the best current spawn for at least one What to Harvest line,
/// each listed once with all 11 stats (highlighted like the resource inventory) and how long it has been up.
/// </summary>
public partial class WthSummaryWindow : Window
{
    static WthSummaryWindow? _open;

    /// <summary>Opens the summary, or brings the one already open to the front (it can be opened from the menu or from What to Harvest).</summary>
    public static void ShowSingle(Window owner)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new WthSummaryWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public WthSummaryWindow()
    {
        InitializeComponent();
        AddStatColumns();
        Services.ActiveGalaxyChanged += Refresh;
        Services.SpawnDataChanged += Refresh;
        Services.TreeChanged += Refresh;
        Services.ProfessionsChanged += Refresh;
        Closed += (_, _) =>
        {
            Services.ActiveGalaxyChanged -= Refresh;
            Services.SpawnDataChanged -= Refresh;
            Services.TreeChanged -= Refresh;
            Services.ProfessionsChanged -= Refresh;
        };
        Loaded += (_, _) => Refresh();
    }

    /// <summary>Same columns and cell colouring as the resource inventory, placed after Planets.</summary>
    void AddStatColumns()
    {
        var baseStyle = TryFindResource(typeof(DataGridCell)) as Style;   // keeps the normal selection look
        int at = 3;
        foreach (var stat in StatInfo.All)
        {
            var style = new Style(typeof(DataGridCell), baseStyle);
            style.Setters.Add(new Setter(Control.BackgroundProperty, new Binding($"Stats[{stat}].Background") { Mode = BindingMode.OneWay }));
            SummaryGrid.Columns.Insert(at++, new DataGridTextColumn
            {
                Header = new TextBlock { Text = stat.ToString(), ToolTip = StatInfo.FullName(stat) },
                Width = 44,
                Binding = new Binding($"Stats[{stat}].Text") { Mode = BindingMode.OneWay },
                CellStyle = style,
                CanUserSort = false,   // the values are text here, so sorting would put 1000 before 999
            });
        }
    }

    const string AllPlanets = "All planets";
    List<WthSummaryRow> _rows = new();
    bool _settingPlanets;

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    void Option_Click(object sender, RoutedEventArgs e) => Refresh();

    void Refresh()
    {
        if (Services.Settings.ActiveGalaxyId is not int gid)
        {
            _rows = new();
            SetPlanetOptions();
            SummaryGrid.ItemsSource = null;
            StatusText.Text = "No active galaxy selected.";
            return;
        }
        var load = Services.StaticData.LoadSchematics();
        if (load.Error != null) { StatusText.Text = "Could not read schematics.json: " + load.Error; return; }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var store = Services.Spawns.ForGalaxy(gid);
            var tree = Services.Tree;
            var needs = HarvestPlanner.Plan(load.Value.Schematics, Services.Settings.IsProfessionEnabled, tree,
                                            store.Spawns(currentOnly: true, null), store.Inventory(), DateTimeOffset.UtcNow);
            bool onlyBetter = OnlyBetterBox.IsChecked == true;

            // each spawn once, however many lines it is best for
            var best = needs.Where(n => n.Best != null && (!onlyBetter || n.BeatsInventory))
                            .Select(n => n.Best!)
                            .GroupBy(c => c.Spawn.Id)
                            .Select(g => g.First())
                            .ToList();

            var labels = ResourceLabels.Build(tree.Document);
            var rows = best.Select(c => new WthSummaryRow(
                    c.Spawn.Name,
                    labels.TryGetValue(c.Spawn.ResourceTypeId, out var l) ? l : $"{c.Spawn.ResourceTypeId} (not in tree)",
                    c.Spawn.Planets,
                    StatHighlight.Cells(c.Spawn, tree),
                    c.DaysUp.ToString(), c.DaysUp))
                .OrderBy(r => r.TypeName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();

            _rows = rows;
            _lineCount = needs.Count(n => n.Best != null);
            SetPlanetOptions();
            ApplyPlanetFilter();
        }
        finally { Mouse.OverrideCursor = null; }
    }

    int _lineCount;

    static IEnumerable<string> PlanetsOf(WthSummaryRow r) =>
        r.Planets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"All planets" plus every planet the listed spawns are on; keeps the current choice when it is still there.</summary>
    void SetPlanetOptions()
    {
        var current = PlanetBox.SelectedItem as string;
        var planets = new List<string> { AllPlanets };
        planets.AddRange(_rows.SelectMany(PlanetsOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        _settingPlanets = true;
        try
        {
            PlanetBox.ItemsSource = planets;
            PlanetBox.SelectedItem = planets.FirstOrDefault(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase)) ?? AllPlanets;
        }
        finally { _settingPlanets = false; }
    }

    void PlanetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingPlanets) ApplyPlanetFilter();
    }

    void ApplyPlanetFilter()
    {
        var planet = PlanetBox.SelectedItem as string ?? AllPlanets;
        var shown = planet == AllPlanets ? _rows
            : _rows.Where(r => PlanetsOf(r).Contains(planet, StringComparer.OrdinalIgnoreCase)).ToList();
        SummaryGrid.ItemsSource = shown;
        StatusText.Text = (planet == AllPlanets ? $"{shown.Count:N0} spawn(s)" : $"{shown.Count:N0} of {_rows.Count:N0} spawn(s) on {planet}") +
                          $" - each the best current spawn for at least one of {_lineCount:N0} What to Harvest line(s).";
    }
}
