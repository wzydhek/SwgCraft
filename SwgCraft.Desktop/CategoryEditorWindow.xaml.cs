using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public partial class CategoryEditorWindow : Window
{
    sealed record IssueRow(string Text, CategoryVm? Vm);

    readonly ChangeTracker _tracker = new();
    readonly ObservableCollection<CategoryVm> _cats = new();
    Dictionary<object, CategoryVm> _map = new(ReferenceEqualityComparer.Instance);

    public CategoryEditorWindow()
    {
        InitializeComponent();
        CatGrid.ItemsSource = _cats;
        _cats.CollectionChanged += (_, _) => UpdateCount();
        _tracker.Changed += () => Title = "Categories" + (_tracker.Dirty ? " *" : "");
        if (!LoadFromDisk()) Loaded += (_, _) => Close();   // never edit on top of a file that failed to load
        UpdateCount();
    }

    CategoryVm? Selected => CatGrid.SelectedItem as CategoryVm;

    void SetStatus(string text) => StatusText.Text = text;

    void Commit() => CatGrid.CommitEdit(DataGridEditingUnit.Row, true);

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadCategories();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Categories", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _cats.Clear();
        foreach (var m in load.Value.Categories) _cats.Add(CategoryVm.FromModel(m, _tracker));
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus(load.FileExisted
            ? $"Loaded {_cats.Count} category(ies) from {Services.StaticData.CategoriesPath}"
            : $"No categories.json yet; it will be created at {Services.StaticData.CategoriesPath}");
        return true;
    }

    CategoriesDocument BuildDocument()
    {
        _map = new Dictionary<object, CategoryVm>(ReferenceEqualityComparer.Instance);
        var doc = new CategoriesDocument();
        foreach (var vm in _cats)
        {
            var m = vm.ToModel();
            _map[m] = vm;
            doc.Categories.Add(m);
        }
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        IssueList.ItemsSource = list.Select(i =>
        {
            CategoryVm? vm = null;
            if (i.Subject != null) _map.TryGetValue(i.Subject, out vm);
            return new IssueRow($"{(i.Severity == Severity.Error ? "ERROR" : "warn ")}  {i.Message}", vm);
        }).ToList();
        SetStatus($"{list.Count(i => i.Severity == Severity.Error)} error(s), {list.Count(i => i.Severity == Severity.Warning)} warning(s)");
    }

    bool Save()
    {
        Commit();
        var doc = BuildDocument();
        SaveResult result;
        try { result = Services.StaticData.SaveCategories(doc); }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Could not write categories.json:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }
        foreach (var vm in _cats) vm.IdFollowsName = false;   // saved IDs are frozen
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        ShowIssues(Services.StaticData.ValidateCategories(BuildDocument()));
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
        Commit();
        if (_tracker.Dirty)
        {
            var r = MessageBox.Show(this, "Save changes to the categories?", "Categories",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ editing

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var vm = CategoryVm.CreateNew(_tracker);
        _cats.Add(vm);
        CatGrid.SelectedItem = vm;
        CatGrid.ScrollIntoView(vm);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CatGrid.CurrentCell = new DataGridCellInfo(vm, CatGrid.Columns[0]);
            CatGrid.BeginEdit();
        }), DispatcherPriority.Background);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } sel) return;
        if (MessageBox.Show(this, $"Delete the category '{sel.Name}'?\n\nSchematics that still use it will be flagged the next time they are validated.",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cats.Remove(sel);
        _tracker.Touch();
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var load = Services.StaticData.LoadSchematics();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var added = new List<string>();
        foreach (var raw in load.Value.Schematics.Select(s => s.Category?.Trim() ?? "").Where(c => c.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool known = _cats.Any(c => string.Equals(c.Id.Trim(), raw, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(c.Name.Trim(), raw, StringComparison.OrdinalIgnoreCase));
            if (known) continue;

            var vm = CategoryVm.CreateNew(_tracker);
            vm.Name = raw;                                   // the ID follows the name
            string baseId = vm.Id, id = baseId;
            for (int n = 2; _cats.Any(c => c.Id == id); n++) id = $"{baseId}_{n}";
            vm.Id = id;
            _cats.Add(vm);
            added.Add(raw);
        }

        SetStatus(added.Count == 0
            ? "Nothing to import: every category used by a schematic is already in the list."
            : $"Added {added.Count} categor{(added.Count == 1 ? "y" : "ies")} from schematics: {string.Join(", ", added)}. Remember to Save.");
    }

    void IssueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueRow { Vm: { } vm })
        {
            CatGrid.SelectedItem = vm;
            CatGrid.ScrollIntoView(vm);
        }
    }

    void UpdateCount()
    {
        int total = _cats.Count;
        CountText.Text = $"{total} {(total == 1 ? "category" : "categories")}";
    }
}
