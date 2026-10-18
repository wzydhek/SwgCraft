using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

/// <summary>A row of the build-items grid: one kind of item with all its uses added up.</summary>
public sealed class BuildItemVm
{
    public BuildItemVm(BuildItem item)
    {
        Item = item;
        bool shortRes = item.Resources.Any(r => r.ShortCrafts > 0 || r.ShortSetups > 0);
        Indent = new string(' ', item.Level * 3);
        ItemText = Indent + item.Label;
        Kind = item.ItemKind switch { BuildItemKind.Class => "class", BuildItemKind.LootItem => "looted", _ => item.IsTarget ? "target" : "crafted" };
        Needed = $"{item.Needed:N0}";
        FromInventory = item.FromInventoryCount > 0 ? $"{item.FromInventoryCount:N0}" : "";
        Craft = item.CraftCount > 0 ? $"{item.CraftCount:N0}" : "";
        How = HowText(item);
        Missing = item.MissingCount > 0 ? $"{item.MissingCount:N0}" : "";
        UsedBy = string.Join(", ", item.UsedBy.Select(u => u.Label));
        Foreground = item.MissingCount > 0 || item.UnmadeInRuns > 0 ? Brushes.Firebrick
                   : shortRes ? Brushes.DarkOrange
                   : item.CraftCount == 0 && item.FromInventoryCount > 0 ? Brushes.DarkGreen
                   : Brushes.Black;
        Weight = item.IsTarget ? FontWeights.Bold : FontWeights.Normal;
    }

    /// <summary>"by hand" / "2 factory runs: 20 + 10 (identical)" / "via Wall Module" ...</summary>
    public static string HowText(BuildItem n)
    {
        if (n.ItemKind == BuildItemKind.Class)
            return n.CraftCount > 0 && n.Recipe != null ? $"made as {n.Recipe.Label}" : n.FromInventoryCount > 0 ? "inventory" : "";
        if (n.CraftCount == 0) return n.FromInventoryCount > 0 ? "inventory" : "";
        if (!n.FactoryRun) return "by hand";
        var why = n.FactoryRequired ? (n.Demands.Any(d => d.FromFactory) ? " (inherited)" : " (identical)") : "";
        var parts = new List<string>();
        if (n.Runs.Count == 1) parts.Add($"1 factory run of {n.Runs[0].Size:N0}{why}");
        else if (n.Runs.Count > 1) parts.Add($"{n.Runs.Count} factory runs: {string.Join(" + ", n.Runs.Select(r => r.Size))}{why}");
        if (n.HandUnits > 0) parts.Add($"{n.HandUnits:N0} by hand");
        if (n.UnmadeInRuns > 0) parts.Add(n.Runs.Count == 0 && n.HandUnits == 0 ? "factory run not possible" + why : $"{n.UnmadeInRuns:N0} not possible");
        return string.Join(", ", parts);
    }

    public BuildItem Item { get; }
    public string Indent { get; }
    public string ItemText { get; }
    public string Kind { get; }
    public string Needed { get; }
    public string FromInventory { get; }
    public string Craft { get; }
    public string How { get; }
    public string Missing { get; }
    public string UsedBy { get; }
    public Brush Foreground { get; }
    public FontWeight Weight { get; }
}

/// <summary>A component slot of the selected item: optional on/off and, for a class slot, the member to use.</summary>
public sealed class SlotChoiceVm
{
    public string Key { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsOptional { get; init; }
    public bool Included { get; init; }
    public string CheckTip => IsOptional ? "Use this optional component" : "Required";
    public List<RefOption> Options { get; init; } = new();
    public string Chosen { get; init; } = "";
    public Visibility ChoiceVisibility => Options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record BuildResourceRow(string Slot, string Resource, string Each, string Crafts, string Spawn, string Type,
                                      string Units, string Value, string Cap, string Price, string Cost, string Short);
public sealed record BuildPullRow(string Spawn, string Type, string Units, int UnitsSort, string Held, int HeldSort,
                                  string Cost, double CostSort, string UsedFor);
public sealed record BuildShortRow(string For, string What, string Amount, string Suggestions);
public sealed record BuildItemRow(string Item, string Serial, string Units, string Held, string UsedFor);

/// <summary>Build > Build Planner: how to build N of a schematic from the active galaxy's inventories. Plans only; nothing is changed.</summary>
public partial class BuildPlannerWindow : Window
{
    readonly SuggestComboBox _targetBox;
    List<RefOption> _targetOptions = new();
    readonly HashSet<string> _forceCraft = new(StringComparer.Ordinal);
    readonly HashSet<string> _factoryToggle = new(StringComparer.Ordinal);
    readonly HashSet<string> _optionalToggle = new(StringComparer.Ordinal);            // optional slots switched against the checkbox
    readonly Dictionary<string, string> _classChoice = new(StringComparer.Ordinal);    // class slot -> member item key
    ComponentClassesDocument _classes = new();
    List<Schematic> _allSchematics = new();
    bool _settingFilters;
    string? _plannedTarget;
    BuildPlan? _plan;
    Dictionary<string, string> _resLabels = new();
    Dictionary<string, string> _itemNames = new();

    public BuildPlannerWindow()
    {
        InitializeComponent();
        _targetBox = new SuggestComboBox(_targetOptions);
        TargetHost.Child = _targetBox;
        Activated += (_, _) => RefreshTargets();
        RefreshTargets();
        StatusText.Text = "Choose what to build and how many, then press Plan. Nothing in your inventories is changed.";
    }

    List<RefOption> _allCategories = new();
    List<RefOption> _allTabs = new();

    /// <summary>Re-reads schematics, professions, tabs and categories (they may have been edited) and rebuilds the filters and the list.</summary>
    void RefreshTargets()
    {
        _allSchematics = Services.StaticData.LoadSchematics().Value.Schematics.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();

        var professions = new List<RefOption> { new("", "All professions") };
        professions.AddRange(Services.StaticData.LoadProfessions().Value.Professions
            .Where(p => !string.IsNullOrEmpty(p.Id))
            .Select(p => new RefOption(p.Id, string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name.Trim())));
        _allCategories = Services.StaticData.LoadCategories().Value.Categories
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .Select(c => new RefOption(c.Id, string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name.Trim())).ToList();
        _allTabs = Services.StaticData.LoadTabs().Value.Tabs
            .Where(t => !string.IsNullOrEmpty(t.Id))
            .Select(t => new RefOption(t.Id, string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name.Trim())).ToList();

        _settingFilters = true;
        try { SetFilterOptions(ProfessionFilterBox, professions); }
        finally { _settingFilters = false; }
        ApplyTargetFilter();
    }

    /// <summary>Sets a filter's list, keeping the current choice if it is still there, else going back to "All".</summary>
    static void SetFilterOptions(ComboBox box, List<RefOption> options)
    {
        var current = (box.SelectedItem as RefOption)?.Key ?? "";
        if (box.ItemsSource is List<RefOption> old && old.SequenceEqual(options)) return;
        box.ItemsSource = options;
        box.SelectedItem = options.FirstOrDefault(o => o.Key == current) ?? options[0];
    }

    void TargetFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingFilters) ApplyTargetFilter();
    }

    static string KeyOf(ComboBox box) => (box.SelectedItem as RefOption)?.Key ?? "";

    /// <summary>
    /// Cascading filters: the Tab list only offers tabs used by the chosen profession's schematics, the Category list only
    /// categories used by schematics of the chosen profession and tab (with counts), so no combination comes out empty.
    /// Then the Build list is narrowed to all three.
    /// </summary>
    void ApplyTargetFilter()
    {
        var prof = KeyOf(ProfessionFilterBox);
        var byProf = _allSchematics.Where(s => prof.Length == 0 || s.Professions.Contains(prof, StringComparer.Ordinal)).ToList();

        _settingFilters = true;
        try
        {
            SetFilterOptions(TabFilterBox, UsedOptions("All tabs", byProf, s => s.Tab, _allTabs));
            var tab = KeyOf(TabFilterBox);
            var byTab = byProf.Where(s => tab.Length == 0 || string.Equals(s.Tab?.Trim(), tab, StringComparison.Ordinal)).ToList();
            SetFilterOptions(CategoryFilterBox, UsedOptions("All categories", byTab, s => s.Category, _allCategories));
        }
        finally { _settingFilters = false; }

        var tabKey = KeyOf(TabFilterBox);
        var cat = KeyOf(CategoryFilterBox);
        var options = byProf
            .Where(s => tabKey.Length == 0 || string.Equals(s.Tab?.Trim(), tabKey, StringComparison.Ordinal))
            .Where(s => cat.Length == 0 || string.Equals(s.Category?.Trim(), cat, StringComparison.Ordinal))
            .Select(s => new RefOption(s.Id, string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name.Trim()))
            .OrderBy(o => o.Display, StringComparer.OrdinalIgnoreCase).ToList();
        StatusText.Text = $"{options.Count:N0} of {_allSchematics.Count:N0} schematics match the filters.";
        if (options.SequenceEqual(_targetOptions)) return;
        _targetOptions = options;
        _targetBox.SetOptions(_targetOptions);
        if (_targetBox.Key.Length > 0 && options.All(o => o.Key != _targetBox.Key)) _targetBox.Key = "";   // filtered out
    }

    /// <summary>"All ..." plus the values the given schematics use, named from the catalog (unknown IDs as-is), with counts.</summary>
    static List<RefOption> UsedOptions(string all, List<Schematic> schematics, Func<Schematic, string?> value, List<RefOption> catalog)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in catalog) names.TryAdd(c.Key, c.Display);
        var list = new List<RefOption> { new("", all) };
        list.AddRange(schematics.Select(s => value(s)?.Trim() ?? "").Where(v => v.Length > 0)
            .GroupBy(v => v, StringComparer.Ordinal)
            .Select(g => new RefOption(g.Key, $"{(names.TryGetValue(g.Key, out var n) ? n : g.Key)} ({g.Count()})"))
            .OrderBy(o => o.Display, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    // ------------------------------------------------------------ planning

    void Plan_Click(object sender, RoutedEventArgs e) => RunPlan(keepForced: false);

    void Option_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, FactoryBox)) _factoryToggle.Clear();   // per-item switches are relative to this default
        if (ReferenceEquals(sender, OptionalBox)) _optionalToggle.Clear();  // per-slot switches too
        if (_plan != null) RunPlan(keepForced: true);
    }

    void RunPlan(bool keepForced)
    {
        if (Services.Settings.ActiveGalaxyId is not int gid) { StatusText.Text = "No active galaxy selected."; return; }
        var target = _targetBox.ResolveKey();
        if (string.IsNullOrEmpty(target)) { StatusText.Text = "Choose a schematic to build (type to search)."; return; }
        if (!int.TryParse(QtyBox.Text.Trim(), out var qty) || qty < 1 || qty > 100_000) { StatusText.Text = "Quantity must be a whole number from 1 to 100,000."; return; }
        if (!keepForced || target != _plannedTarget)
        {
            _forceCraft.Clear();
            _factoryToggle.Clear();
            _optionalToggle.Clear();
            _classChoice.Clear();
        }

        var schematics = Services.StaticData.LoadSchematics();
        var classes = Services.StaticData.LoadClasses();
        var loot = Services.StaticData.LoadLootItems();
        var error = schematics.Error ?? classes.Error ?? loot.Error;
        if (error != null) { StatusText.Text = error; return; }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var store = Services.Spawns.ForGalaxy(gid);
            var inputs = new BuildInputs(
                new SchematicCatalog(schematics.Value), new ClassCatalog(classes.Value), new LootCatalog(loot.Value), Services.Tree,
                store.Inventory(), store.ItemInventory(), store.Spawns(currentOnly: true, null), Services.Settings.IsProfessionEnabled);
            var options = new BuildOptions(IgnoreExpBox.IsChecked == true, OptionalBox.IsChecked == true, new HashSet<string>(_forceCraft),
                                           FactoryBox.IsChecked == true, new HashSet<string>(_factoryToggle),
                                           new HashSet<string>(_optionalToggle), new Dictionary<string, string>(_classChoice));
            _classes = classes.Value;
            _plan = BuildPlanner.Plan(inputs, target, qty, options);
            _plannedTarget = target;

            _resLabels = ResourceLabels.Build(Services.Tree.Document);
            _itemNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in schematics.Value.Schematics) _itemNames.TryAdd("Schematic:" + s.Id, string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name);
            foreach (var l in loot.Value.Items) _itemNames.TryAdd("LootItem:" + l.Id, string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name);

            ShowPlan();
        }
        finally { Mouse.OverrideCursor = null; }
    }

    string ResLabel(string id) =>
        _resLabels.TryGetValue(id, out var l) ? (Services.Tree.Find(id) is { IsLeaf: false } ? l + " (any)" : l) : id;

    string ItemName(ItemStack s) => _itemNames.TryGetValue($"{s.Kind}:{s.ItemId}", out var n) ? n : s.ItemId;

    void ShowPlan()
    {
        if (_plan == null) return;
        var keepKey = (ItemGrid.SelectedItem as BuildItemVm)?.Item.Key ?? _plan.Target.Key;
        var rows = _plan.Items.Select(i => new BuildItemVm(i)).ToList();
        ItemGrid.ItemsSource = rows;
        var keep = rows.FirstOrDefault(r => r.Item.Key == keepKey) ?? rows.FirstOrDefault();
        ItemGrid.SelectedItem = keep;
        if (keep != null) { ItemGrid.ScrollIntoView(keep); ShowItem(keep.Item); }

        var slots = _plan.Items.SelectMany(n => n.Resources.Select(r => (Node: n, Slot: r))).ToList();

        // resources to pull, per spawn
        PullGrid.ItemsSource = slots.SelectMany(x => x.Slot.Takes.Select(t => (x.Node, x.Slot, Take: t)))
            .GroupBy(x => x.Take.Stack.Id)
            .Select(g =>
            {
                var first = g.First().Take.Stack;
                int units = g.Sum(x => x.Take.Quantity);
                double? cost = g.All(x => x.Take.Cost != null) ? g.Sum(x => x.Take.Cost!.Value) : null;
                return new BuildPullRow(first.Spawn.Name, ResLabel(first.Spawn.ResourceTypeId), $"{units:N0}", units,
                    $"{first.Quantity:N0}", first.Quantity, cost is double c ? $"{c:N1}" : "(no price)", cost ?? -1,
                    string.Join(", ", g.Select(x => x.Node.Label).Distinct()));
            })
            .OrderBy(r => r.Type, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Spawn).ToList();

        // shortages: resources and items
        var shorts = slots.Where(x => x.Slot.ShortCrafts > 0 || x.Slot.ShortSetups > 0).Select(x => new BuildShortRow(
                x.Node.Label, $"{ResLabel(x.Slot.ResourceId)} (slot {x.Slot.SlotIndex + 1})",
                $"{x.Slot.ShortAmount:N0} ({x.Slot.ShortCrafts + x.Slot.ShortSetups:N0} x {x.Slot.AmountEach:N0}{(x.Slot.ShortSetups > 0 ? ", incl. the manufacturing schematic" : "")})",
                x.Slot.Suggestions.Count == 0 ? "nothing suitable is spawning"
                    : string.Join(";  ", x.Slot.Suggestions.Select(s => $"{s.Spawn.Name} {s.Value:0}{(s.CapPercent is double p ? $" ({p:0}%)" : "")} - {s.Spawn.Planets}"))))
            .Concat(_plan.Items.Where(n => n.MissingCount > 0).Select(n => new BuildShortRow(
                string.Join(", ", n.UsedBy.Select(u => u.Label)) is { Length: > 0 } by ? by : "(target)",
                n.Label, $"{n.MissingCount:N0} item(s)", n.Problem ?? "")))
            .ToList();
        ShortGrid.ItemsSource = shorts;

        // items from inventory
        ItemsGrid.ItemsSource = _plan.Items.SelectMany(n => n.FromInventory.Select(t => (Node: n, Take: t)))
            .GroupBy(x => x.Take.Stack.Id)
            .Select(g =>
            {
                var s = g.First().Take.Stack;
                return new BuildItemRow(ItemName(s), s.Serial ?? "", $"{g.Sum(x => x.Take.Quantity):N0}", $"{s.Quantity:N0}",
                                        string.Join(", ", g.SelectMany(x => x.Node.UsedBy).Select(u => u.Label).Distinct()));
            }).ToList();

        int pullCount = ((System.Collections.ICollection)PullGrid.ItemsSource).Count;
        PullTab.Header = $"Resources to pull ({pullCount})";
        ShortTab.Header = shorts.Count == 0 ? "Shortages (none)" : $"Shortages ({shorts.Count})";
        ItemsTab.Header = $"Items from inventory ({((System.Collections.ICollection)ItemsGrid.ItemsSource).Count})";

        StepsButton.IsEnabled = true;
        var t = _plan.Target;
        StatusText.Text = _plan.Complete
            ? $"Everything for {t.Needed:N0} x {t.Label} is on hand."
            : $"{t.Needed:N0} x {t.Label}: {shorts.Count} shortage(s) - see the Shortages tab.";
        CostText.Text = $"Resource cost {_plan.KnownCost:N1}" + (_plan.CostIsComplete ? "" : " (some stacks have no price)");
    }

    // ------------------------------------------------------------ selected item

    void ItemGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemGrid.SelectedItem is BuildItemVm vm) ShowItem(vm.Item);
    }

    BuildItem? _shown;

    void ShowItem(BuildItem n)
    {
        _shown = n;
        NodeTitle.Text = $"{n.Needed:N0} x {n.Label}";
        var info = new List<string>();
        var uses = n.Demands.Where(d => d.From != n).GroupBy(d => d.From)
            .Select(g => $"{g.Sum(d => d.Units):N0} for {g.Key.Label}" + (g.Any(d => d.Identical) ? $" (identical groups of {string.Join("/", g.Where(d => d.Identical).Select(d => d.GroupSize).Distinct())})" : ""))
            .ToList();
        if (uses.Count > 0) info.Add("Needed: " + string.Join("; ", uses) + ".");
        if (n.FromInventoryCount > 0) info.Add($"{n.FromInventoryCount:N0} from item inventory.");
        if (n.ItemKind == BuildItemKind.Class && n.CraftCount > 0 && n.Recipe != null)
            info.Add($"The other {n.CraftCount:N0} are made as {n.Recipe.Label} (see that row).");
        if (n.ItemKind == BuildItemKind.Schematic && n.CraftCount > 0 && n.Schematic != null)
        {
            info.Add($"Craft {n.CraftCount:N0} x {n.Schematic.Name}; experimentation {(n.QualityMatters ? "matters: best spawns first (for an \"any\" slot, general-purpose spawns before types other schematics want specifically)" : "does not matter here: general-purpose spawns, biggest stack first")}.");
            if (n.FactoryRun)
            {
                info.Add($"Factory run{(n.FactoryReason != null ? " because " + n.FactoryReason : "")}. " +
                         (n.Runs.Count == 0 ? "No run is possible with the stock held." : $"Runs: {string.Join(" + ", n.Runs.Select(r => r.Size))}.") +
                         " Every unit of a run uses the same spawn for each resource slot, and everything it uses is factory-made too.");
                var groups = n.Runs.Select((r, i) => (Run: r, Index: i)).Where(x => x.Run.Groups.Count > 0)
                    .Select(x => $"run {x.Index + 1}: {string.Join(", ", x.Run.Groups.Select(g => $"{g.Units:N0} for {g.From.Label}"))}").ToList();
                if (groups.Count > 0) info.Add("Identical sets: " + string.Join("; ", groups) + ".");
                info.Add("Each run also uses one unit's worth of resources and components to make its manufacturing schematic.");
                if (n.HandUnits > 0) info.Add($"{n.HandUnits:N0} made by hand instead: a factory run of one would only waste a unit's worth.");
            }
            else info.Add("Hand-crafted: each unit can use a different spawn." +
                          (n.CraftCount == 1 && FactoryBox.IsChecked == true ? " (Only one is needed, so no factory run: it would waste a unit's worth on the manufacturing schematic.)" : ""));
            if (n.Components.Count > 0)
                info.Add("Uses per unit: " + string.Join(", ", n.Components.Select(c => $"{c.PerUnit} x {c.Child.Label}{(c.Slots > 1 ? $" ({c.Slots} slots)" : "")}")) + ".");
        }
        if (n.SkippedOptional.Count > 0) info.Add("Optional, not planned: " + string.Join(", ", n.SkippedOptional) + ".");
        if (n.Problem != null) info.Add(n.Problem);
        NodeInfo.Text = string.Join("  ", info);
        NodeInfo.Foreground = n.MissingCount > 0 ? Brushes.Firebrick : Brushes.Black;

        bool forced = _forceCraft.Contains(n.Key);
        ForceCraftButton.Visibility = !n.IsTarget && n.CanForceCraft && (forced || n.FromInventoryCount > 0) ? Visibility.Visible : Visibility.Collapsed;
        ForceCraftButton.Content = forced ? "Use item inventory again" : "Craft new instead of using inventory";

        FactoryButton.Visibility = n.ItemKind == BuildItemKind.Schematic && n.CraftCount >= 2 && !n.FactoryRequired ? Visibility.Visible : Visibility.Collapsed;   // a run of one only wastes a unit's worth
        FactoryButton.Content = n.FactoryRun ? "Hand-craft instead of a factory run" : "Make it a factory run";
        FactoryButton.ToolTip = "Switch this item between factory run(s) and hand-crafting. A factory run also makes everything it uses in factory runs.";

        SlotList.ItemsSource = n.SlotChoices.Select(SlotVm).ToList();
        SlotBox.Visibility = n.SlotChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // slot choices only matter for units that get crafted
        string? slotNote = n.SlotChoices.Count == 0 || n.CraftCount > 0 ? null
            : n.FromInventoryCount > 0
                ? $"All {n.FromInventoryCount:N0} come from item inventory, so none are crafted and these slots aren't planned. " +
                  "Press \"Craft new instead of using inventory\" to build new ones with these choices."
                : "None are crafted, so these slots aren't planned.";
        SlotNote.Text = slotNote ?? "";
        SlotNote.Visibility = slotNote != null ? Visibility.Visible : Visibility.Collapsed;

        ItemTakeBox.Visibility = n.FromInventory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemTakeList.ItemsSource = n.FromInventory.Select(t =>
            $"{t.Quantity:N0} x {ItemName(t.Stack)}{(t.Stack.Serial != null ? $" (serial {t.Stack.Serial})" : "")} of {t.Stack.Quantity:N0} held" +
            (string.IsNullOrWhiteSpace(t.Stack.Note) ? "" : " - " + string.Join("; ", t.Stack.Note.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))).ToList();

        var rows = new List<BuildResourceRow>();
        foreach (var r in n.Resources)
        {
            string label = ResLabel(r.ResourceId);
            string shortText = r.ShortCrafts > 0 || r.ShortSetups > 0 ? $"{r.ShortAmount:N0} short ({r.ShortCrafts} unit(s))" : "";
            if (r.Takes.Count == 0)
                rows.Add(new BuildResourceRow($"{r.SlotIndex + 1}", label, $"{r.AmountEach:N0}", $"{r.Crafts:N0}", "(none held)", "", "", "", "", "", "", shortText));
            bool first = true;
            foreach (var t in r.Takes)
            {
                rows.Add(new BuildResourceRow(
                    first ? $"{r.SlotIndex + 1}" : "", first ? label : "", first ? $"{r.AmountEach:N0}" : "",
                    n.FactoryRun && r.Takes.IndexOf(t) < n.Runs.Count ? $"run of {t.Crafts:N0} + 1" : $"{t.Crafts:N0}",
                    t.Stack.Spawn.Name, ResLabel(t.Stack.Spawn.ResourceTypeId) + (t.Specific ? " (type-specific)" : ""), $"{t.Quantity:N0}",
                    r.QualityMatters ? $"{t.Value:0}" : "", r.QualityMatters && t.CapPercent is double p ? $"{p:0}%" : "",
                    t.Stack.PricePerUnit is double pr ? $"{pr:N2}" : "", t.Cost is double c ? $"{c:N1}" : "",
                    first ? shortText : ""));
                first = false;
            }
        }
        ResourceGrid.ItemsSource = rows;
    }

    SlotChoiceVm SlotVm(BuildSlotChoice c)
    {
        var options = new List<RefOption>();
        if (c.ClassId != null)
        {
            options.Add(new RefOption("", "Any member (held first, then the default recipe)"));
            var cls = _classes.Classes.FirstOrDefault(x => x.Id == c.ClassId);
            if (cls != null)
                options.AddRange(cls.Members.Select(m => new RefOption(BuildPlanner.MemberKey(m.Kind, m.Id),
                    (_itemNames.TryGetValue($"{m.Kind}:{m.Id}", out var name) ? name : m.Id) + (m.Kind == MemberKind.LootItem ? " (looted)" : ""))));
        }
        int q = Math.Max(1, c.Slot.Quantity);
        var what = c.ClassId != null ? $"{c.TargetLabel} (class)" : c.TargetLabel;
        return new SlotChoiceVm
        {
            Key = c.Key,
            Text = $"Slot {c.SlotIndex + 1}: {q} x {what}" + (c.Slot.Identical && q >= 2 ? ", identical" : "") +
                   (c.Slot.Optional ? (c.Included ? " - optional, used" : " - optional, not used") : ""),
            IsOptional = c.Slot.Optional,
            Included = c.Included,
            Options = options,
            Chosen = c.ChosenMember ?? "",
        };
    }

    void SlotIncluded_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SlotChoiceVm vm || !vm.IsOptional) return;
        if (!_optionalToggle.Remove(vm.Key)) _optionalToggle.Add(vm.Key);
        RunPlan(keepForced: true);
    }

    void SlotChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // the first selection comes from the binding when the row is shown; only a change by the user re-plans
        if (e.RemovedItems.Count == 0 || sender is not ComboBox box || box.DataContext is not SlotChoiceVm vm) return;
        var key = box.SelectedValue as string ?? "";
        if (key == vm.Chosen) return;
        if (key.Length == 0) _classChoice.Remove(vm.Key); else _classChoice[vm.Key] = key;
        Dispatcher.BeginInvoke(new Action(() => RunPlan(keepForced: true)));   // not while the combo box is still handling the click
    }

    void Steps_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        var steps = BuildSteps.From(_plan, ResLabel, ItemName);
        var report = BuildSteps.ToText(_plan, steps, ResLabel, ItemName);
        var summary = $"{steps.Count} step(s) for {_plan.Target.Needed:N0} x {_plan.Target.Label}" +
                      (_plan.Complete ? "" : " - some things are short or missing");
        var file = string.Concat($"build steps - {_plan.Target.Needed} x {_plan.Target.Label}".Split(System.IO.Path.GetInvalidFileNameChars())) + ".txt";
        new BuildStepsWindow(report, summary, file) { Owner = this }.Show();
    }

    void Factory_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        if (!_factoryToggle.Remove(_shown.Key)) _factoryToggle.Add(_shown.Key);
        RunPlan(keepForced: true);
    }

    void ForceCraft_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        if (!_forceCraft.Remove(_shown.Key)) _forceCraft.Add(_shown.Key);
        RunPlan(keepForced: true);
    }
}
