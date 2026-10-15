using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

/// <summary>Static Data > Tabs: the SWGEmu crafting tabs (tabs.json). Same layout and behaviour as the Categories editor.</summary>
public partial class TabEditorWindow : Window
{
    sealed record IssueRow(string Text, TabVm? Vm);

    readonly ChangeTracker _tracker = new();
    readonly ObservableCollection<TabVm> _tabs = new();
    Dictionary<object, TabVm> _map = new(ReferenceEqualityComparer.Instance);

    public TabEditorWindow()
    {
        InitializeComponent();
        TabGrid.ItemsSource = _tabs;
        _tabs.CollectionChanged += (_, _) => UpdateCount();
        _tracker.Changed += () => Title = "Tabs" + (_tracker.Dirty ? " *" : "");
        if (!LoadFromDisk()) Loaded += (_, _) => Close();   // never edit on top of a file that failed to load
        UpdateCount();
    }

    TabVm? Selected => TabGrid.SelectedItem as TabVm;

    void SetStatus(string text) => StatusText.Text = text;

    void Commit() => TabGrid.CommitEdit(DataGridEditingUnit.Row, true);

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadTabs();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Tabs", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _tabs.Clear();
        foreach (var m in load.Value.Tabs) _tabs.Add(TabVm.FromModel(m, _tracker));
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus(load.FileExisted
            ? $"Loaded {_tabs.Count} tab(s) from {Services.StaticData.TabsPath}"
            : $"No tabs.json yet; it will be created at {Services.StaticData.TabsPath}");
        return true;
    }

    TabsDocument BuildDocument()
    {
        _map = new Dictionary<object, TabVm>(ReferenceEqualityComparer.Instance);
        var doc = new TabsDocument();
        foreach (var vm in _tabs)
        {
            var m = vm.ToModel();
            _map[m] = vm;
            doc.Tabs.Add(m);
        }
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        IssueList.ItemsSource = list.Select(i =>
        {
            TabVm? vm = null;
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
        try { result = Services.StaticData.SaveTabs(doc); }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Could not write tabs.json:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }
        foreach (var vm in _tabs) vm.IdFollowsName = false;   // saved IDs are frozen
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        ShowIssues(Services.StaticData.ValidateTabs(BuildDocument()));
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
            var r = MessageBox.Show(this, "Save changes to the tabs?", "Tabs",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ editing

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var vm = TabVm.CreateNew(_tracker);
        _tabs.Add(vm);
        TabGrid.SelectedItem = vm;
        TabGrid.ScrollIntoView(vm);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TabGrid.CurrentCell = new DataGridCellInfo(vm, TabGrid.Columns[0]);
            TabGrid.BeginEdit();
        }), DispatcherPriority.Background);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } sel) return;
        if (MessageBox.Show(this, $"Delete the tab '{sel.Name}'?\n\nSchematics that still use it will be flagged the next time they are validated.",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _tabs.Remove(sel);
        _tracker.Touch();
    }

    void IssueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueRow { Vm: { } vm })
        {
            TabGrid.SelectedItem = vm;
            TabGrid.ScrollIntoView(vm);
        }
    }

    void UpdateCount()
    {
        int total = _tabs.Count;
        CountText.Text = $"{total} {(total == 1 ? "tab" : "tabs")}";
    }
}
