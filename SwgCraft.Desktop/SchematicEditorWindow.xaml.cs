using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public partial class SchematicEditorWindow : Window
{
    sealed record IssueRow(string Text, SchematicVm? Vm);

    readonly ChangeTracker _tracker = new();
    readonly ObservableCollection<SchematicVm> _schematics = new();
    readonly ICollectionView _view;
    readonly PickerColumn _componentCol;
    readonly PickerColumn _resourceCol;
    readonly PickerColumn _skillCol;
    List<RefOption> _skillOptions = new();
    Dictionary<object, SchematicVm> _map = new(ReferenceEqualityComparer.Instance);

    List<RefOption> _componentOptions = new();
    List<RefOption> _resourceOptions = new();
    List<Profession> _professions = new();
    List<CraftCategory> _categories = new();
    List<RefOption> _categoryOptions = new();
    readonly SuggestComboBox _categoryBox;
    List<CraftTab> _tabs = new();
    List<RefOption> _tabOptions = new();
    readonly SuggestComboBox _tabBox;

    public SchematicEditorWindow()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_schematics);
        _view.Filter = o => PassesFilters((SchematicVm)o);
        SchematicList.ItemsSource = _view;
        _schematics.CollectionChanged += (_, _) => UpdateCount();

        _componentCol = new PickerColumn("Component (schematic, class or looted item) - type to search", nameof(ComponentSlotVm.Key));
        ComponentGrid.Columns.Insert(1, _componentCol);   // after the slot name
        _resourceCol = new PickerColumn("Resource - type to search", nameof(ResourceSlotVm.Resource));
        ResourceGrid.Columns.Insert(1, _resourceCol);     // after the slot name
        _skillCol = new PickerColumn("Skill - type to search", nameof(SkillSlotVm.Skill));
        SkillGrid.Columns.Add(_skillCol);
        BuildExperimentColumns();

        _categoryBox = new SuggestComboBox(_categoryOptions)
        {
            AllowEmpty = true,
            ToolTip = "Type to search the category list (Static Data > Categories). Clear the box for no category.",
        };
        _categoryBox.SetBinding(SuggestComboBox.KeyProperty, new Binding(nameof(SchematicVm.Category)) { Mode = BindingMode.TwoWay });
        CategoryHost.Child = _categoryBox;

        _tabBox = new SuggestComboBox(_tabOptions)
        {
            AllowEmpty = true,
            ToolTip = "The SWGEmu crafting tab this schematic is listed under. Type to search the tab list (Static Data > Tabs). Clear the box for no tab.",
        };
        _tabBox.SetBinding(SuggestComboBox.KeyProperty, new Binding(nameof(SchematicVm.Tab)) { Mode = BindingMode.TwoWay });
        TabHost.Child = _tabBox;

        _tracker.Changed += () => Title = "Schematics" + (_tracker.Dirty ? " *" : "");
        Activated += (_, _) => RefreshOptions();
        if (!LoadFromDisk()) Loaded += (_, _) => Close();   // never edit on top of a file that failed to load
        UpdateCount();
    }

    /// <summary>The eleven stat columns and the Total check, on the modifiers grid.</summary>
    void BuildExperimentColumns()
    {
        foreach (var stat in StatInfo.All)
        {
            ModifierGrid.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { Text = stat.ToString(), ToolTip = StatInfo.FullName(stat) },
                Width = 40,
                Binding = new Binding(stat.ToString()) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            });
        }

        var warn = new Style(typeof(TextBlock));
        var trigger = new DataTrigger { Binding = new Binding("TotalOk"), Value = false };
        trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Red));
        trigger.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
        warn.Triggers.Add(trigger);
        ModifierGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Total",
            Width = 46,
            IsReadOnly = true,
            Binding = new Binding("Total") { Mode = BindingMode.OneWay },
            ElementStyle = warn,
        });
    }

    SchematicVm? Selected => SchematicList.SelectedItem as SchematicVm;

    void SetStatus(string text) => StatusText.Text = text;

    void CommitAll()
    {
        ComponentGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ResourceGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ExperimentGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ModifierGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SkillGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    // ------------------------------------------------------------ picker options

    List<RefOption> BuildComponentOptions()
    {
        var list = new List<RefOption>();
        foreach (var s in Services.StaticData.LoadSchematics().Value.Schematics)
            if (!string.IsNullOrEmpty(s.Id))
                list.Add(new RefOption(ComponentSlotVm.KeyFor(SlotKind.Schematic, s.Id), "Schematic: " + (string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name)));
        foreach (var c in Services.StaticData.LoadClasses().Value.Classes)
            if (!string.IsNullOrEmpty(c.Id))
                list.Add(new RefOption(ComponentSlotVm.KeyFor(SlotKind.Class, c.Id), "Class: " + (string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name)));
        foreach (var l in Services.StaticData.LoadLootItems().Value.Items)
            if (!string.IsNullOrEmpty(l.Id))
                list.Add(new RefOption(ComponentSlotVm.KeyFor(SlotKind.LootItem, l.Id), "Looted: " + (string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name)));
        list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    static List<RefOption> BuildResourceOptions()
    {
        var labels = ResourceLabels.Build(Services.Tree.Document);
        var list = new List<RefOption>();
        void Walk(ResourceNode n)
        {
            if (!string.IsNullOrEmpty(n.Id) && labels.TryGetValue(n.Id, out var label))
                list.Add(new RefOption(n.Id, n.IsLeaf ? label : label + " (any)"));   // a group means "any resource below it"
            if (n.Children != null) foreach (var c in n.Children) Walk(c);
        }
        foreach (var root in Services.Tree.Document.Nodes) Walk(root);
        return list;   // tree order, so related resources stay together
    }

    static List<CraftCategory> BuildCategories() => Services.StaticData.LoadCategories().Value.Categories.ToList();

    static List<RefOption> CategoryOptionsFor(List<CraftCategory> categories) =>
        categories.Where(c => !string.IsNullOrEmpty(c.Id))
                  .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                  .Select(c => new RefOption(c.Id, string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name))
                  .ToList();

    static bool SameCategories(List<CraftCategory> a, List<CraftCategory> b) =>
        a.Select(c => (c.Id, c.Name)).SequenceEqual(b.Select(c => (c.Id, c.Name)));

    static List<CraftTab> BuildTabs() => Services.StaticData.LoadTabs().Value.Tabs.ToList();

    static List<RefOption> TabOptionsFor(List<CraftTab> tabs) =>
        tabs.Where(t => !string.IsNullOrEmpty(t.Id))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new RefOption(t.Id, string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name))
            .ToList();

    static bool SameTabs(List<CraftTab> a, List<CraftTab> b) =>
        a.Select(t => (t.Id, t.Name)).SequenceEqual(b.Select(t => (t.Id, t.Name)));

    static List<Profession> BuildProfessions() => Services.StaticData.LoadProfessions().Value.Professions.ToList();

    static bool SameProfessions(List<Profession> a, List<Profession> b) =>
        a.Select(p => (p.Id, p.Name)).SequenceEqual(b.Select(p => (p.Id, p.Name))) &&
        a.SelectMany(p => p.Skills).Select(s => (s.Id, s.Name)).SequenceEqual(b.SelectMany(p => p.Skills).Select(s => (s.Id, s.Name)));

    /// <summary>
    /// Just the skill's name ("Novice Artisan"), in profession order then skill order. Skill names are unique, but if two ever
    /// match, those two get their profession added ("Novice [Artisan]") so the list never shows the same text twice.
    /// </summary>
    static List<RefOption> SkillOptionsFor(List<Profession> professions)
    {
        var skills = professions.SelectMany(p => p.Skills
                                    .Where(s => !string.IsNullOrEmpty(s.Id))
                                    .Select(s => (Prof: string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name.Trim(),
                                                  s.Id,
                                                  Name: string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name.Trim())))
                                .ToList();
        var shared = skills.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                           .Where(g => g.Count() > 1)
                           .Select(g => g.Key)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return skills.Select(s => new RefOption(s.Id, shared.Contains(s.Name) ? $"{s.Name} [{s.Prof}]" : s.Name)).ToList();
    }

    /// <summary>Called when the window is activated: picks up classes, schematics, resources or professions edited in other windows.</summary>
    void RefreshOptions()
    {
        var components = BuildComponentOptions();
        var resources = BuildResourceOptions();
        var professions = BuildProfessions();
        var categories = BuildCategories();
        var tabs = BuildTabs();

        bool tabsChanged = !SameTabs(tabs, _tabs);
        bool categoriesChanged = !SameCategories(categories, _categories);
        bool componentsChanged = !components.SequenceEqual(_componentOptions);
        bool resourcesChanged = !resources.SequenceEqual(_resourceOptions);
        bool professionsChanged = !SameProfessions(professions, _professions);
        if (!componentsChanged && !resourcesChanged && !professionsChanged && !categoriesChanged && !tabsChanged) return;

        CommitAll();
        if (tabsChanged)
        {
            _tabs = tabs;
            _tabOptions = TabOptionsFor(_tabs);
            _tabBox.SetOptions(_tabOptions);
        }
        if (categoriesChanged)
        {
            _categories = categories;
            _categoryOptions = CategoryOptionsFor(_categories);
            _categoryBox.SetOptions(_categoryOptions);
            foreach (var s in _schematics) s.ResolveCategory(_categories);
        }
        if (componentsChanged)
        {
            _componentOptions = components;
            _componentCol.SetOptions(_componentOptions);
            foreach (var s in _schematics) foreach (var c in s.Components) c.RaiseKey();
        }
        if (resourcesChanged)
        {
            _resourceOptions = resources;
            _resourceCol.SetOptions(_resourceOptions);
            foreach (var s in _schematics) foreach (var r in s.Resources) r.RaiseResource();
        }
        if (professionsChanged)
        {
            _professions = professions;
            _skillOptions = SkillOptionsFor(_professions);
            _skillCol.SetOptions(_skillOptions);
            foreach (var s in _schematics)
            {
                s.SetProfessionCatalog(_professions);
                foreach (var sk in s.Skills) sk.RaiseSkill();
            }
            SetProfessionFilterOptions();
        }
    }

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadSchematics();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Schematics", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _componentOptions = BuildComponentOptions();
        _resourceOptions = BuildResourceOptions();
        _professions = BuildProfessions();
        _skillOptions = SkillOptionsFor(_professions);
        _skillCol.SetOptions(_skillOptions);
        SetProfessionFilterOptions();
        _categories = BuildCategories();
        _categoryOptions = CategoryOptionsFor(_categories);
        _categoryBox.SetOptions(_categoryOptions);
        _tabs = BuildTabs();
        _tabOptions = TabOptionsFor(_tabs);
        _tabBox.SetOptions(_tabOptions);
        _componentCol.SetOptions(_componentOptions);
        _resourceCol.SetOptions(_resourceOptions);

        SchematicList.SelectedItem = null;
        _schematics.Clear();
        foreach (var s in load.Value.Schematics)
        {
            var vm = SchematicVm.FromModel(s, _professions, _tracker);
            vm.ResolveCategory(_categories);   // older files may hold the category's name; switch it to the ID
            _schematics.Add(vm);
        }
        SortInPlace();
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus(load.FileExisted
            ? $"Loaded {_schematics.Count} schematic(s) from {Services.StaticData.SchematicsPath}"
            : $"No schematics.json yet; it will be created at {Services.StaticData.SchematicsPath}");
        return true;
    }

    void SortInPlace()
    {
        var selected = Selected;
        var sorted = _schematics.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = _schematics.IndexOf(sorted[i]);
            if (at != i) _schematics.Move(at, i);
        }
        if (selected == null) return;
        SchematicList.SelectedItem = selected;

        // Bring it into view once the list has redrawn in its new order (scrolling straight away would use the old positions).
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SchematicList.SelectedItem == selected) SchematicList.ScrollIntoView(selected);
        }), DispatcherPriority.Loaded);
    }

    SchematicsDocument BuildDocument()
    {
        _map = new Dictionary<object, SchematicVm>(ReferenceEqualityComparer.Instance);
        var doc = new SchematicsDocument();
        foreach (var vm in _schematics)
        {
            var m = vm.ToModel();
            _map[m] = vm;
            doc.Schematics.Add(m);
        }
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        IssueList.ItemsSource = list.Select(i =>
        {
            SchematicVm? vm = null;
            if (i.Subject != null) _map.TryGetValue(i.Subject, out vm);
            return new IssueRow($"{(i.Severity == Severity.Error ? "ERROR" : "warn ")}  {i.Message}", vm);
        }).ToList();
        SetStatus($"{list.Count(i => i.Severity == Severity.Error)} error(s), {list.Count(i => i.Severity == Severity.Warning)} warning(s)");
    }

    bool Save()
    {
        CommitAll();
        var doc = BuildDocument();
        SaveResult result;
        try { result = Services.StaticData.SaveSchematics(doc); }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Could not write schematics.json:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }
        foreach (var vm in _schematics) vm.IdFollowsName = false;   // saved IDs are frozen
        SortInPlace();
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        ShowIssues(Services.StaticData.ValidateSchematics(BuildDocument()));
    }

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker.Dirty &&
            MessageBox.Show(this, "Discard unsaved changes and reload from disk?", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        LoadFromDisk();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitAll();
        if (_tracker.Dirty)
        {
            var r = MessageBox.Show(this, "Save changes to the schematics?", "Schematics",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ schematic list

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();

    // ------------------------------------------------------------ list filters (name text + profession)

    /// <summary>The profession ID chosen in the filter above the list, or "" for all professions.</summary>
    string ProfessionFilterId => (ProfessionFilter.SelectedItem as RefOption)?.Key ?? "";

    bool PassesFilters(SchematicVm s)
    {
        var text = FilterBox.Text?.Trim() ?? "";
        if (text.Length > 0 && !s.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return false;
        var prof = ProfessionFilterId;
        return prof.Length == 0 || s.SelectedProfessionIds().Contains(prof, StringComparer.Ordinal);
    }

    void ApplyFilters()
    {
        if (_view == null) return;   // the combo box can report a change while the window is still being built
        _view.Refresh();
        UpdateCount();
        if (Selected is { } s) SchematicList.ScrollIntoView(s);
    }

    void ProfessionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    /// <summary>"All professions" plus the profession list, in its usual order. Keeps the current choice if it still exists.</summary>
    void SetProfessionFilterOptions()
    {
        var current = ProfessionFilterId;
        var options = new List<RefOption> { new("", "All professions") };
        options.AddRange(_professions.Where(p => !string.IsNullOrEmpty(p.Id))
                                     .Select(p => new RefOption(p.Id, string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name)));
        ProfessionFilter.ItemsSource = options;
        ProfessionFilter.SelectedItem = options.FirstOrDefault(o => o.Key == current) ?? options[0];
    }

    void SchematicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Detail.DataContext = Selected;
        Detail.IsEnabled = Selected != null;
        if (Selected is { Experiments.Count: > 0 } s) ExperimentGrid.SelectedItem = s.Experiments[0];   // so its modifiers show right away
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        FilterBox.Text = "";
        var vm = SchematicVm.CreateNew(_tracker, _professions);
        // while the list is filtered by profession, the new schematic gets that profession, so it stays in the list
        if (ProfessionFilterId is { Length: > 0 } prof && vm.Professions.FirstOrDefault(p => p.Id == prof) is { } pick)
            pick.IsSelected = true;
        _schematics.Add(vm);
        SchematicList.SelectedItem = vm;
        SchematicList.ScrollIntoView(vm);
        ShowResourcesTab();
        FocusName();
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        if (Selected is not { } sel) return;
        FilterBox.Text = "";
        var model = sel.ToModel();
        model.Name += " (copy)";
        model.Id = Slug.From(model.Name);
        var vm = SchematicVm.FromModel(model, _professions, _tracker);
        vm.IdFollowsName = true;
        _schematics.Add(vm);
        _tracker.Touch();
        SchematicList.SelectedItem = vm;
        SchematicList.ScrollIntoView(vm);
        ShowResourcesTab();
        FocusName();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        if (Selected is not { } sel) return;
        if (MessageBox.Show(this, $"Delete the schematic '{sel.Name}'?\n\nClasses and other schematics that still use it will be flagged the next time they are validated.",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _schematics.Remove(sel);
        _tracker.Touch();
    }

    void IssueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueRow { Vm: { } vm })
        {
            FilterBox.Text = "";
            if (!PassesFilters(vm)) ProfessionFilter.SelectedIndex = 0;   // the profession filter was hiding it
            SchematicList.SelectedItem = vm;
            SchematicList.ScrollIntoView(vm);
        }
    }

    // ------------------------------------------------------------ slot rows (shared by the three grids)

    /// <param name="caretAtEnd">Put the cursor after any text already in the first cell instead of selecting it all.</param>
    void AddRow(DataGrid grid, IList list, object item, bool caretAtEnd = false)
    {
        CommitAll();
        list.Add(item);
        grid.SelectedItem = item;
        grid.ScrollIntoView(item);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            grid.CurrentCell = new DataGridCellInfo(item, grid.Columns[0]);
            grid.BeginEdit();
            if (caretAtEnd && grid.Columns[0].GetCellContent(item) is TextBox tb)
            {
                // the grid selects the whole text when editing starts; undo that now and again once focus has settled
                void PutCaret()
                {
                    tb.Focus();
                    tb.Select(tb.Text.Length, 0);
                }
                PutCaret();
                Dispatcher.BeginInvoke(new Action(PutCaret), DispatcherPriority.Input);
            }
        }), DispatcherPriority.Background);
    }

    void DeleteRow(DataGrid grid, IList list)
    {
        CommitAll();
        if (grid.SelectedItem is { } item) list.Remove(item);
    }

    void MoveRow(DataGrid grid, IList list, int delta)
    {
        CommitAll();
        if (grid.SelectedItem is not { } item) return;
        int i = list.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.RemoveAt(i);
        list.Insert(j, item);
        grid.SelectedItem = item;
    }

    void AddComponent_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) AddRow(ComponentGrid, s.Components, new ComponentSlotVm(_tracker)); }
    void DeleteComponent_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) DeleteRow(ComponentGrid, s.Components); }
    void ComponentUp_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) MoveRow(ComponentGrid, s.Components, -1); }
    void ComponentDown_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) MoveRow(ComponentGrid, s.Components, 1); }

    void AddSkill_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) AddRow(SkillGrid, s.Skills, new SkillSlotVm(_tracker)); }
    void DeleteSkill_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) DeleteRow(SkillGrid, s.Skills); }

    void AddResource_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) AddRow(ResourceGrid, s.Resources, new ResourceSlotVm(_tracker)); }
    void DeleteResource_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) DeleteRow(ResourceGrid, s.Resources); }
    void ResourceUp_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) MoveRow(ResourceGrid, s.Resources, -1); }
    void ResourceDown_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) MoveRow(ResourceGrid, s.Resources, 1); }

    void AddExperiment_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var vm = ExperimentVm.CreateNew(_tracker);   // named "Experimental ", with one modifier that takes the rest of the name as you type it
        AddRow(ExperimentGrid, s.Experiments, vm, caretAtEnd: true);
    }
    void DeleteExperiment_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) DeleteRow(ExperimentGrid, s.Experiments); }
    void ExperimentUp_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) MoveRow(ExperimentGrid, s.Experiments, -1); }
    void ExperimentDown_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) MoveRow(ExperimentGrid, s.Experiments, 1); }

    ExperimentVm? SelectedExperiment => ExperimentGrid.SelectedItem as ExperimentVm;

    void AddModifier_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExperiment is not { } x)
        {
            SetStatus("Select an experimental first, then add its modifiers.");
            return;
        }
        AddRow(ModifierGrid, x.Modifiers, new ModifierVm(_tracker));
    }
    void DeleteModifier_Click(object sender, RoutedEventArgs e) { if (SelectedExperiment is { } x) DeleteRow(ModifierGrid, x.Modifiers); }
    void ModifierUp_Click(object sender, RoutedEventArgs e) { if (SelectedExperiment is { } x) MoveRow(ModifierGrid, x.Modifiers, -1); }
    void ModifierDown_Click(object sender, RoutedEventArgs e) { if (SelectedExperiment is { } x) MoveRow(ModifierGrid, x.Modifiers, 1); }

    /// <summary>Puts the cursor in the Name box with its text selected, so the new name can be typed straight over it.</summary>
    /// <summary>A new or duplicated schematic starts on the Resources tab, whatever tab was showing before.</summary>
    void ShowResourcesTab() => DetailTabs.SelectedItem = ResourcesTab;

    /// <summary>Tabbing into the box selects its contents, so typing replaces them.</summary>
    void SelectAllOnFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    /// <summary>The first click into the box selects its contents too (instead of just placing the cursor); later clicks behave normally.</summary>
    void SelectAllOnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBox { IsKeyboardFocusWithin: false } tb)
        {
            e.Handled = true;
            tb.Focus();   // raises GotKeyboardFocus, which selects everything
        }
    }

    void FocusName() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }), DispatcherPriority.Input);

    // ------------------------------------------------------------ Fill from clipboard

    // Columns of the copied line (6 tab-separated values). Column 3 is not used.
    const int FillName = 0, FillId = 1, FillSkill = 2, FillComplexity = 4, FillSize = 5, FillColumns = 6;

    /// <summary>
    /// Overwrites the selected schematic's name, ID, complexity, size and skill with one tab-separated line from the clipboard.
    /// The copied skill may be cut short ("Installations III"), so it matches the skill whose name starts with it.
    /// </summary>
    // ------------------------------------------------------------ fill from a SWGEmu draft schematic script

    SchematicScriptIndex? _scriptIndex;
    StringCatalog? _strings;

    void FromScript_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        if (Selected is not { } vm)
        {
            SetStatus("From Script: select a schematic first.");
            return;
        }

        string? file = null;
        while (true)
        {
            // the scripts folder (asked for once, then remembered in the settings)
            var folder = Services.Settings.SchematicScriptsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                if (!ChooseScriptsFolder()) return;
                continue;
            }
            if (_scriptIndex == null || !string.Equals(_scriptIndex.Root, folder, StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try { _scriptIndex = new SchematicScriptIndex(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show(this, "Could not read the scripts folder:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                finally { System.Windows.Input.Mouse.OverrideCursor = null; }
            }

            file ??= _scriptIndex.Find(vm.Id?.Trim() ?? "");
            if (file == null)
            {
                var ask = MessageBox.Show(this, $"No script in {folder} matches the ID '{vm.Id}'.\n\nPick the .lua file for it?", Title,
                                          MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes || (file = PickScriptFile(folder)) == null) return;
            }

            SchematicScript script;
            try { script = SchematicScriptReader.ReadFile(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, "Could not read the script:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            ScriptMergeResult result;
            try
            {
                _strings ??= new StringCatalog(Services.StaticData.LoadStrings().Value);
                var all = new SchematicsDocument();
                all.Schematics.AddRange(_schematics.Select(s => s.ToModel()));
                result = SchematicScriptMerge.Merge(vm.ToModel(), script, _strings, new SchematicCatalog(all),
                                                    new ClassCatalog(Services.StaticData.LoadClasses().Value), Services.Tree, _scriptIndex);
            }
            finally { System.Windows.Input.Mouse.OverrideCursor = null; }

            var report = new System.Text.StringBuilder();
            void Section(string title, List<string> lines, string none)
            {
                report.AppendLine(title);
                if (lines.Count == 0) report.AppendLine("  " + none);
                foreach (var l in lines) report.AppendLine("  - " + l);
                report.AppendLine();
            }
            Section("WILL BE FILLED IN / ADDED", result.Changes, "nothing - everything the script has is already entered");
            Section("DIFFERENT FROM THE SCRIPT (left as entered)", result.Differences, "none");
            if (result.Notes.Count > 0) Section("NOTES", result.Notes, "");

            var rel = Path.GetRelativePath(folder, file);
            var header = $"{vm.Name} ({vm.Id})  <-  {rel}" + (script.CustomObjectName is { Length: > 0 } n ? $"   [script name: {n}]" : "");
            var dlg = new ScriptImportWindow(header, report.ToString(), result.Changes.Count > 0) { Owner = this };
            dlg.ShowDialog();
            switch (dlg.Choice)
            {
                case ScriptImportChoice.Apply:
                    ApplyScriptMerge(vm, result.Merged);
                    SetStatus($"From Script: {result.Changes.Count} change(s) made to {vm.Name} - save to keep them.");
                    return;
                case ScriptImportChoice.ChangeFolder:
                    if (!ChooseScriptsFolder()) return;
                    file = null;
                    continue;
                case ScriptImportChoice.PickFile:
                    var picked = PickScriptFile(Path.GetDirectoryName(file) ?? folder);
                    if (picked == null) return;
                    file = picked;
                    continue;
                default:
                    return;
            }
        }
    }

    // ------------------------------------------------------------ reports

    static string Describe(Schematic s) => $"{(string.IsNullOrWhiteSpace(s.Name) ? "(no name)" : s.Name.Trim())} ({s.Id})";

    /// <summary>Shows a report; <paramref name="refresh"/> builds it again for the window's Refresh button (null: keep what is shown).</summary>
    void ShowReport(string title, string summary, string report, string fileName, Func<Task<(string Text, string Summary)?>>? refresh = null)
    {
        new BuildStepsWindow(report, summary, fileName, refresh) { Owner = this, Title = title }.Show();
    }

    /// <summary>Schematics with a resource or component slot that has no slot name.</summary>
    void MissingNamesReport_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        var r = ComputeMissingNames();
        ShowReport("Missing Slot Names", r.Summary, r.Text, "missing slot names.txt",
                   () => { CommitAll(); return Task.FromResult<(string Text, string Summary)?>(ComputeMissingNames()); });
    }

    (string Text, string Summary) ComputeMissingNames()
    {
        var rows = _schematics.Select(v => v.ToModel())
            .Select(s => (S: s,
                          Res: s.Resources.Select((r, i) => (r, i)).Where(x => string.IsNullOrWhiteSpace(x.r.Name)).Select(x => x.i + 1).ToList(),
                          Comp: s.Components.Select((c, i) => (c, i)).Where(x => string.IsNullOrWhiteSpace(x.c.Name)).Select(x => x.i + 1).ToList()))
            .Where(x => x.Res.Count + x.Comp.Count > 0)
            .OrderBy(x => x.S.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int slots = rows.Sum(x => x.Res.Count + x.Comp.Count);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCHEMATICS WITH SLOTS WITHOUT A NAME: {rows.Count:N0} of {_schematics.Count:N0} schematics, {slots:N0} slots");
        sb.AppendLine($"Fully named: {_schematics.Count - rows.Count:N0}. (Schematics with no slots at all count as fully named.)");
        sb.AppendLine();
        foreach (var (s, res, comp) in rows)
        {
            var parts = new List<string>();
            if (res.Count > 0) parts.Add((res.Count == s.Resources.Count ? "all resource slots" : "resource slot" + (res.Count > 1 ? "s " : " ") + string.Join(", ", res)) + $" (of {s.Resources.Count})");
            if (comp.Count > 0) parts.Add((comp.Count == s.Components.Count ? "all component slots" : "component slot" + (comp.Count > 1 ? "s " : " ") + string.Join(", ", comp)) + $" (of {s.Components.Count})");
            sb.AppendLine($"{Describe(s)}: {string.Join("; ", parts)}");
        }
        return (sb.ToString(), $"{rows.Count:N0} schematic(s), {slots:N0} slot(s) without a name");
    }

    /// <summary>Every schematic compared with its SWGEmu script: where they disagree, what could be filled in, and which have no script.</summary>
    async void ScriptDiffReport_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        IsEnabled = false;
        (string Text, string Summary)? r;
        try { r = await ComputeScriptDiffAsync(); }
        finally { IsEnabled = true; }
        if (r is { } report)
            ShowReport("Script Differences", report.Summary, report.Text, "script differences.txt", () => { CommitAll(); return ComputeScriptDiffAsync(); });
    }

    async Task<(string Text, string Summary)?> ComputeScriptDiffAsync()
    {
        CommitAll();
        var folder = Services.Settings.SchematicScriptsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            if (!ChooseScriptsFolder()) return null;
            folder = Services.Settings.SchematicScriptsFolder!;
        }

        var models = _schematics.Select(v => v.ToModel()).ToList();
        var all = new SchematicsDocument();
        all.Schematics.AddRange(models);
        var classes = new ClassCatalog(Services.StaticData.LoadClasses().Value);
        var tree = Services.Tree;
        var index = _scriptIndex is { } ix && string.Equals(ix.Root, folder, StringComparison.OrdinalIgnoreCase) ? ix : null;

        SetStatus("Comparing every schematic with its script...");
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var report = await Task.Run(() =>
            {
                index ??= new SchematicScriptIndex(folder);
                _strings ??= new StringCatalog(Services.StaticData.LoadStrings().Value);
                var strings = _strings;
                var catalog = new SchematicCatalog(all);

                var noScript = new List<Schematic>();
                var unreadable = new List<string>();
                var differing = new List<(Schematic S, ScriptMergeResult R)>();
                int fillable = 0, matching = 0;
                foreach (var s in models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var file = index.Find(s.Id?.Trim() ?? "");
                    if (file == null) { noScript.Add(s); continue; }
                    ScriptMergeResult r;
                    try { r = SchematicScriptMerge.Merge(s, SchematicScriptReader.ReadFile(file), strings, catalog, classes, tree, index); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable.Add($"{Describe(s)}: {ex.Message}"); continue; }
                    if (r.Changes.Count > 0) fillable++;
                    // a script whose slot lists don't line up is a disagreement worth seeing too
                    var problems = r.Notes.Where(n => n.StartsWith("The script's slot lists", StringComparison.Ordinal)).ToList();
                    if (r.Differences.Count + problems.Count > 0) differing.Add((s, r));
                    else matching++;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"SCHEMATICS COMPARED WITH THE SWGEMU SCRIPTS IN {folder}");
                sb.AppendLine($"{models.Count:N0} schematics: {differing.Count:N0} differ, {matching:N0} agree, {noScript.Count:N0} have no script" +
                              (unreadable.Count > 0 ? $", {unreadable.Count:N0} unreadable" : "") + ".");
                sb.AppendLine($"{fillable:N0} of them have something From Script could fill in (empty slot names, missing slots, empty complexity or size).");
                sb.AppendLine("Nothing has been changed.");
                sb.AppendLine();

                sb.AppendLine($"DIFFERENCES ({differing.Count:N0})");
                foreach (var (s, r) in differing)
                {
                    sb.AppendLine(Describe(s) + "  <-  " + Path.GetRelativePath(folder, index.Find(s.Id!.Trim())!));
                    foreach (var note in r.Notes.Where(x => x.StartsWith("The script's slot lists", StringComparison.Ordinal))) sb.AppendLine("    ! " + note);
                    foreach (var d in r.Differences) sb.AppendLine("    - " + d);
                }
                sb.AppendLine();

                sb.AppendLine($"NO SCRIPT FOUND FOR THE ID ({noScript.Count:N0})");
                foreach (var s in noScript) sb.AppendLine("    " + Describe(s));
                if (unreadable.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"COULD NOT BE READ ({unreadable.Count:N0})");
                    foreach (var u in unreadable) sb.AppendLine("    " + u);
                }
                return (Text: sb.ToString(), Summary: $"{differing.Count:N0} differ, {matching:N0} agree, {noScript.Count:N0} without a script");
            });
            _scriptIndex = index;
            SetStatus("Script Differences: " + report.Summary + ".");
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not read the scripts:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("Script Differences: could not read the scripts.");
            return null;
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    ObjectTemplateIndex? _objectIndex;
    LootScriptIndex? _lootScripts;
    string? _objectIndexFolder;

    /// <summary>What fits each kind of component slot (from the object scripts) against the component classes entered.</summary>
    async void SlotCandidatesReport_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();
        IsEnabled = false;
        (string Text, string Summary)? r;
        try { r = await ComputeSlotCandidatesAsync(); }
        finally { IsEnabled = true; }
        if (r is { } report)
            ShowReport("Slot Candidates", report.Summary, report.Text, "slot candidates.txt", () => { CommitAll(); return ComputeSlotCandidatesAsync(); });
    }

    async Task<(string Text, string Summary)?> ComputeSlotCandidatesAsync()
    {
        CommitAll();
        var folder = Services.Settings.SchematicScriptsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            if (!ChooseScriptsFolder()) return null;
            folder = Services.Settings.SchematicScriptsFolder!;
        }
        // the object scripts sit next to draft_schematic: scripts/object/tangible
        var objectFolder = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? folder, "tangible");
        if (!Directory.Exists(objectFolder))
        {
            MessageBox.Show(this, $"The object scripts were not found at\n{objectFolder}\n\nThe scripts folder should be ...\\scripts\\object\\draft_schematic, " +
                                  "with ...\\scripts\\object\\tangible next to it.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var models = _schematics.Select(v => v.ToModel()).ToList();
        var classes = new ClassCatalog(Services.StaticData.LoadClasses().Value);
        var loot = new LootCatalog(Services.StaticData.LoadLootItems().Value);
        var index = _scriptIndex is { } ix && string.Equals(ix.Root, folder, StringComparison.OrdinalIgnoreCase) ? ix : null;
        bool sameFolder = string.Equals(_objectIndexFolder, objectFolder, StringComparison.OrdinalIgnoreCase);
        var objects = sameFolder ? _objectIndex : null;
        var lootScripts = sameFolder ? _lootScripts : null;
        // scripts/object/tangible -> scripts
        var scriptsRoot = Path.GetDirectoryName(Path.GetDirectoryName(objectFolder)) ?? objectFolder;

        SetStatus("Reading the object scripts and matching slots (the first time takes a little while)...");
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var report = await Task.Run(() =>
            {
                index ??= new SchematicScriptIndex(folder);
                objects ??= new ObjectTemplateIndex(objectFolder);
                lootScripts ??= new LootScriptIndex(scriptsRoot);
                _strings ??= new StringCatalog(Services.StaticData.LoadStrings().Value);
                return SlotCandidateReport.Build(models, index, objects, _strings, classes, loot, lootScripts);
            });
            _scriptIndex = index;
            _objectIndex = objects;
            _lootScripts = lootScripts;
            _objectIndexFolder = objectFolder;
            SetStatus("Slot Candidates: " + report.Summary + ".");
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not read the scripts:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("Slot Candidates: could not read the scripts.");
            return null;
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    /// <summary>Puts the filled-in slots, complexity and size into the schematic being edited (not saved yet).</summary>
    void ApplyScriptMerge(SchematicVm vm, Schematic merged)
    {
        vm.Resources.Clear();
        foreach (var r in merged.Resources) vm.Resources.Add(ResourceSlotVm.FromModel(r, _tracker));
        vm.Components.Clear();
        foreach (var c in merged.Components) vm.Components.Add(ComponentSlotVm.FromModel(c, _tracker));
        if (vm.Complexity == null && merged.Complexity != null) vm.Complexity = merged.Complexity;
        if (vm.Size == null && merged.Size != null) vm.Size = merged.Size;
        _tracker.Touch();
    }

    bool ChooseScriptsFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the SWGEmu draft_schematic scripts folder (...\\scripts\\object\\draft_schematic)",
            InitialDirectory = Services.Settings.SchematicScriptsFolder is { } f && Directory.Exists(f) ? f : "",
        };
        if (dlg.ShowDialog(this) != true) return false;
        var folder = dlg.FolderName;
        // pointing at the scripts root (or object) instead of draft_schematic itself is forgiven
        foreach (var sub in new[] { Path.Combine("object", "draft_schematic"), "draft_schematic" })
            if (Directory.Exists(Path.Combine(folder, sub))) { folder = Path.Combine(folder, sub); break; }
        Services.Settings.SchematicScriptsFolder = folder;
        try { SettingsStore.Save(Services.Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("Could not save the settings: " + ex.Message); }
        _scriptIndex = null;
        return true;
    }

    string? PickScriptFile(string folder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the draft schematic script",
            Filter = "Lua scripts (*.lua)|*.lua|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(folder) ? folder : "",
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    void Fill_Click(object sender, RoutedEventArgs e)
    {
        CommitAll();   // finish any cell being edited first, so it can't overwrite what we fill in
        if (Selected is not { } vm)
        {
            SetStatus("Fill: select a schematic first.");
            return;
        }

        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.COMException) { text = ""; }   // the clipboard is busy in another program

        // first non-empty line only; empty columns are kept so the positions don't shift
        var line = text.Split('\n').Select(l => l.Trim('\r')).FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var parts = line.Split('\t').Select(p => p.Trim()).ToArray();
        if (parts.Length < FillColumns)
        {
            SetStatus($"Fill: the clipboard needs one line of {FillColumns} tab-separated values (found {(line.Length == 0 ? 0 : parts.Length)}).");
            return;
        }

        var problems = new List<string>();

        if (parts[FillName].Length > 0) vm.Name = parts[FillName];
        else problems.Add("no name");

        if (parts[FillId].Length > 0) vm.Id = parts[FillId];   // set after the name, so a new schematic's ID doesn't follow it
        else problems.Add("no ID");

        if (int.TryParse(parts[FillComplexity], out var complexity)) vm.Complexity = complexity;
        else problems.Add($"complexity '{parts[FillComplexity]}' is not a number");

        if (int.TryParse(parts[FillSize], out var size)) vm.Size = size;
        else problems.Add($"size '{parts[FillSize]}' is not a number");

        // the skill list is replaced by the one copied skill (when it can be found)
        var copiedSkill = parts[FillSkill];
        var matches = FindSkillsStartingWith(copiedSkill);
        vm.Skills.Clear();
        if (matches.Count == 1)
        {
            var slot = new SkillSlotVm(_tracker);
            vm.Skills.Add(slot);
            slot.Skill = matches[0].Id;   // set after adding, so its profession gets ticked like a hand-picked skill
        }
        else if (copiedSkill.Length == 0) problems.Add("no skill");
        else if (matches.Count == 0) problems.Add($"no skill starts with '{copiedSkill}'");
        else problems.Add($"'{copiedSkill}' matches {matches.Count} skills ({string.Join(", ", matches.Select(m => m.Name))}); pick one");

        SetStatus(problems.Count == 0
            ? $"Filled '{vm.Name}' from the clipboard."
            : $"Filled '{vm.Name}' from the clipboard, except: {string.Join("; ", problems)}.");
    }

    /// <summary>
    /// Skills whose name is the copied text, or starts with it followed by a space (ignoring case).
    /// The space matters: "Installations I" must find "Installations I Factories", not "Installations II ..." as well.
    /// An exact name wins over longer ones.
    /// </summary>
    List<ProfessionSkill> FindSkillsStartingWith(string copied)
    {
        copied = copied.Trim();
        if (copied.Length == 0) return new List<ProfessionSkill>();
        var all = _professions.SelectMany(p => p.Skills).Where(s => !string.IsNullOrEmpty(s.Id)).ToList();
        var exact = all.Where(s => string.Equals(s.Name?.Trim(), copied, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return all.Where(s => (s.Name ?? "").Trim().StartsWith(copied + " ", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Right-hand end of the status bar: how many schematics there are, and how many the filter is showing.</summary>
    void UpdateCount()
    {
        int total = _schematics.Count;
        int shown = _view.Cast<object>().Count();
        CountText.Text = shown == total
            ? $"{total} schematic{(total == 1 ? "" : "s")}"
            : $"{shown} of {total} schematics shown";
    }
}
