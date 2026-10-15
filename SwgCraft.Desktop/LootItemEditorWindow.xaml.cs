using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public partial class LootItemEditorWindow : Window
{
    sealed record IssueRow(string Text, LootItemVm? Vm);

    readonly ChangeTracker _tracker = new();
    readonly ObservableCollection<LootItemVm> _items = new();
    Dictionary<object, LootItemVm> _map = new(ReferenceEqualityComparer.Instance);

    public LootItemEditorWindow()
    {
        InitializeComponent();
        ItemGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateCount();
        _tracker.Changed += () => Title = "Looted Items" + (_tracker.Dirty ? " *" : "");
        if (!LoadFromDisk()) Loaded += (_, _) => Close();   // never edit on top of a file that failed to load
        UpdateCount();
    }

    LootItemVm? Selected => ItemGrid.SelectedItem as LootItemVm;

    void SetStatus(string text) => StatusText.Text = text;

    void Commit() => ItemGrid.CommitEdit(DataGridEditingUnit.Row, true);

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadLootItems();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Looted Items", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _items.Clear();
        foreach (var m in load.Value.Items) _items.Add(LootItemVm.FromModel(m, _tracker));
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus(load.FileExisted
            ? $"Loaded {_items.Count} looted item(s) from {Services.StaticData.LootItemsPath}"
            : $"No lootitems.json yet; it will be created at {Services.StaticData.LootItemsPath}");
        return true;
    }

    LootItemsDocument BuildDocument()
    {
        _map = new Dictionary<object, LootItemVm>(ReferenceEqualityComparer.Instance);
        var doc = new LootItemsDocument();
        foreach (var vm in _items)
        {
            var m = vm.ToModel();
            _map[m] = vm;
            doc.Items.Add(m);
        }
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        IssueList.ItemsSource = list.Select(i =>
        {
            LootItemVm? vm = null;
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
        try { result = Services.StaticData.SaveLootItems(doc); }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Could not write lootitems.json:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }
        foreach (var vm in _items) vm.IdFollowsName = false;   // saved IDs are frozen
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        ShowIssues(Services.StaticData.ValidateLootItems(BuildDocument()));
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
            var r = MessageBox.Show(this, "Save changes to the looted items?", "Looted Items",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ editing

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var vm = LootItemVm.CreateNew(_tracker);
        _items.Add(vm);
        ItemGrid.SelectedItem = vm;
        ItemGrid.ScrollIntoView(vm);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ItemGrid.CurrentCell = new DataGridCellInfo(vm, ItemGrid.Columns[0]);
            ItemGrid.BeginEdit();
        }), DispatcherPriority.Background);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } sel) return;
        if (MessageBox.Show(this, $"Delete the looted item '{sel.Name}'?\n\nAny class that still lists it will be flagged the next time that class list is validated.",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _items.Remove(sel);
        _tracker.Touch();
    }

    void IssueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueRow { Vm: { } vm })
        {
            ItemGrid.SelectedItem = vm;
            ItemGrid.ScrollIntoView(vm);
        }
    }

    void UpdateCount()
    {
        int total = _items.Count;
        CountText.Text = $"{total} {(total == 1 ? "looted item" : "looted items")}";
    }
}
