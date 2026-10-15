using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

/// <summary>A line of the What to Harvest grid.</summary>
public sealed record HarvestRow(
    HarvestNeed Need, string Resource, bool IsSpecific, string Stats, string UsedBy, string UsedByAll,
    string BestSpawn, string BestType, string Planets, string Value, double ValueSort, string Cap, double CapSort, int CapTier,
    string Days, string MyBest, string Gain, double GainSort, bool Beats, string SearchText);

/// <summary>A line of the "best current spawns" grid under it.</summary>
public sealed record HarvestCandidateRow(
    string Spawn, string Type, string StatsUsed, string Value, double ValueSort, string Cap, double CapSort, int CapTier,
    string Days, string Planets);

/// <summary>Resources > What to Harvest: what is spawning now that the enabled professions' schematics want most.</summary>
public partial class HarvestWindow : Window
{
    List<HarvestRow> _rows = new();

    public HarvestWindow()
    {
        InitializeComponent();
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

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void Summary_Click(object sender, RoutedEventArgs e) => WthSummaryWindow.ShowSingle(Owner ?? this);

    // ------------------------------------------------------------ build the list

    void Refresh()
    {
        if (Services.Settings.ActiveGalaxyId is not int gid)
        {
            _rows = new();
            ApplyFilters();
            StatusText.Text = "No active galaxy selected.";
            return;
        }

        var schematicsLoad = Services.StaticData.LoadSchematics();
        if (schematicsLoad.Error != null)
        {
            StatusText.Text = "Could not read schematics.json: " + schematicsLoad.Error;
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var store = Services.Spawns.ForGalaxy(gid);
            var tree = Services.Tree;
            var schematics = schematicsLoad.Value.Schematics;
            var needs = HarvestPlanner.Plan(schematics, Services.Settings.IsProfessionEnabled, tree,
                                            store.Spawns(currentOnly: true, null), store.Inventory(), DateTimeOffset.UtcNow);

            var labels = ResourceLabels.Build(tree.Document);
            string Label(string id) =>
                labels.TryGetValue(id, out var l) ? (tree.Find(id) is { IsLeaf: false } ? l + " (any)" : l) : id;

            _rows = needs.Select(n => ToRow(n, Label, tree.Find(n.ResourceId) is { IsLeaf: true }))
                         .OrderBy(r => r.Resource, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Stats, StringComparer.Ordinal)
                         .ToList();

            int used = schematics.Count(s => !s.IgnoreExperimentation && s.Experiments.Count > 0
                                             && s.Professions.Any(p => Services.Settings.IsProfessionEnabled(p)));
            var sync = store.LastSuccessfulSync();
            StatusText.Text = $"{needs.Count:N0} resource needs from {used:N0} schematic(s) of the professions you include. " +
                              (sync == null ? "No feed sync yet." : $"Last feed sync {sync.Value.ToLocalTime():g}.");
        }
        finally { Mouse.OverrideCursor = null; }
        ApplyFilters();
    }

    static HarvestRow ToRow(HarvestNeed n, Func<string, string> label, bool isSpecific)
    {
        var best = n.Best;
        var uses = n.Uses.Select(u => u.SchematicName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string usedBy = uses.Count switch
        {
            0 => "",
            1 => uses[0],
            _ => $"{uses.Count}: {string.Join(", ", uses)}",
        };
        string usedByAll = string.Join("\n", n.Uses.Select(UseText));

        string myBest = n.BestStock is { } st ? $"{st.Item.Spawn.Name} {st.Value:0} (x{st.Item.Quantity:N0})" : "-";
        string gain = best == null ? "" : n.BestStock == null ? "new" : $"{n.Gain:+0;-0;0}";

        var search = string.Join(" ", new[] { label(n.ResourceId), n.WeightsText, best?.Spawn.Name ?? "" }
                                       .Concat(n.Uses.Select(u => u.SchematicName)));
        return new HarvestRow(
            n, label(n.ResourceId), isSpecific, n.WeightsText, usedBy, usedByAll,
            best?.Spawn.Name ?? "(nothing spawning)", best == null ? "" : label(best.Spawn.ResourceTypeId),
            best?.Spawn.Planets ?? "",
            best == null ? "" : $"{best.Value:0}", best?.Value ?? -1,
            best?.CapPercent is double c ? $"{c:0}%" : "", best?.CapPercent ?? -1, Tier(best?.CapPercent),
            best == null ? "" : best.DaysUp.ToString(),
            myBest, gain, n.Gain ?? double.MinValue, n.BeatsInventory, search);
    }

    static string UseText(HarvestUse u)
    {
        var parts = new List<string> { u.SchematicName };
        if (u.Experimental.Length > 0) parts.Add(u.Experimental);
        if (u.Modifier.Length > 0 && !string.Equals(u.Modifier, ExperimentVm.ModifierNameFor(u.Experimental), StringComparison.OrdinalIgnoreCase))
            parts.Add(u.Modifier);
        return string.Join(" - ", parts);
    }

    /// <summary>The green highlight tiers used in the resource inventory: 90% and 95% of the type's cap.</summary>
    static int Tier(double? capPercent) => capPercent is double p ? StatQuality.Tier((int)Math.Round(p * 10), 1000) : 0;

    // ------------------------------------------------------------ filters

    void Option_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilters();

    void ApplyFilters()
    {
        var keep = (NeedGrid.SelectedItem as HarvestRow)?.Need;
        var text = SearchBox.Text?.Trim() ?? "";
        bool onlyBetter = OnlyBetterBox.IsChecked == true;
        bool showEmpty = ShowEmptyBox.IsChecked == true;

        var shown = _rows.Where(r =>
                (showEmpty || r.Need.Best != null) &&
                (!onlyBetter || r.Beats) &&
                (text.Length == 0 || r.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        NeedGrid.ItemsSource = shown;
        if (keep != null) NeedGrid.SelectedItem = shown.FirstOrDefault(r => r.Need == keep);
        CountText.Text = shown.Count == _rows.Count ? $"{_rows.Count:N0} line(s)" : $"{shown.Count:N0} of {_rows.Count:N0} line(s) shown";
        ShowDetail();
    }

    // ------------------------------------------------------------ detail of the selected line

    void NeedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    void ShowDetail()
    {
        if (NeedGrid.SelectedItem is not HarvestRow row)
        {
            UseList.ItemsSource = null;
            CandidateGrid.ItemsSource = null;
            return;
        }
        var labels = ResourceLabels.Build(Services.Tree.Document);
        UseList.ItemsSource = row.Need.Uses.Select(UseText).ToList();
        CandidateGrid.ItemsSource = row.Need.Candidates.Select(c => new HarvestCandidateRow(
            c.Spawn.Name,
            labels.TryGetValue(c.Spawn.ResourceTypeId, out var l) ? l : c.Spawn.ResourceTypeId,
            string.Join(" ", row.Need.Weights.Keys.Select(s => $"{s} {(c.Spawn.Stats.TryGetValue(s, out var v) ? v : 0)}")),
            $"{c.Value:0}", c.Value,
            c.CapPercent is double p ? $"{p:0}%" : "", c.CapPercent ?? -1, Tier(c.CapPercent),
            c.DaysUp.ToString(), c.Spawn.Planets)).ToList();
    }
}
