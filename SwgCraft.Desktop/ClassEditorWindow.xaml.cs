using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public partial class ClassEditorWindow : Window
{
    sealed record IssueRow(string Text, ClassVm? Vm);

    readonly ChangeTracker _tracker = new();
    readonly ObservableCollection<ClassVm> _classes = new();
    readonly ICollectionView _view;
    readonly PickerColumn _memberCol;
    Dictionary<object, ClassVm> _map = new(ReferenceEqualityComparer.Instance);
    List<RefOption> _options = new();

    public ClassEditorWindow()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_classes);
        _view.Filter = o =>
        {
            var f = FilterBox.Text?.Trim() ?? "";
            return f.Length == 0 || ((ClassVm)o).Name.Contains(f, StringComparison.OrdinalIgnoreCase);
        };
        ClassList.ItemsSource = _view;
        _classes.CollectionChanged += (_, _) => UpdateCount();

        _memberCol = new PickerColumn("Member (schematic or looted item) - type to search", nameof(ClassMemberVm.Key));
        MemberGrid.Columns.Add(_memberCol);

        _tracker.Changed += () => Title = "Component Classes" + (_tracker.Dirty ? " *" : "");
        Activated += (_, _) => RefreshOptions();
        if (!LoadFromDisk()) Loaded += (_, _) => Close();   // never edit on top of a file that failed to load
        UpdateCount();
    }

    ClassVm? Selected => ClassList.SelectedItem as ClassVm;

    void SetStatus(string text) => StatusText.Text = text;

    void Commit() => MemberGrid.CommitEdit(DataGridEditingUnit.Row, true);

    // ------------------------------------------------------------ picker options

    string? BuildOptions(out List<RefOption> options)
    {
        options = new List<RefOption>();
        var schematics = Services.StaticData.LoadSchematics();
        var loot = Services.StaticData.LoadLootItems();
        foreach (var s in schematics.Value.Schematics)
            if (!string.IsNullOrEmpty(s.Id))
                options.Add(new RefOption(ClassMemberVm.KeyFor(MemberKind.Schematic, s.Id), "Schematic: " + (string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name)));
        foreach (var l in loot.Value.Items)
            if (!string.IsNullOrEmpty(l.Id))
                options.Add(new RefOption(ClassMemberVm.KeyFor(MemberKind.LootItem, l.Id), "Loot: " + (string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name)));
        options.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return schematics.Error ?? loot.Error;
    }

    void RefreshOptions()
    {
        BuildOptions(out var fresh);
        if (fresh.Count == _options.Count && fresh.Zip(_options).All(p => p.First == p.Second)) return;   // nothing changed
        Commit();
        _options = fresh;
        _memberCol.SetOptions(_options);
        foreach (var c in _classes) foreach (var m in c.Members) m.RaiseKey();
    }

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadClasses();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Component Classes", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        var optionsError = BuildOptions(out _options);
        _memberCol.SetOptions(_options);

        ClassList.SelectedItem = null;
        _classes.Clear();
        foreach (var c in load.Value.Classes) _classes.Add(ClassVm.FromModel(c, _tracker));
        SortInPlace();
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus((load.FileExisted
            ? $"Loaded {_classes.Count} class(es) from {Services.StaticData.ClassesPath}"
            : $"No componentclasses.json yet; it will be created at {Services.StaticData.ClassesPath}")
            + (optionsError != null ? "   (a members file could not be read; see Validate)" : ""));
        return true;
    }

    void SortInPlace()
    {
        var selected = Selected;
        var sorted = _classes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = _classes.IndexOf(sorted[i]);
            if (at != i) _classes.Move(at, i);
        }
        if (selected != null) ClassList.SelectedItem = selected;
    }

    ComponentClassesDocument BuildDocument()
    {
        _map = new Dictionary<object, ClassVm>(ReferenceEqualityComparer.Instance);
        var doc = new ComponentClassesDocument();
        foreach (var vm in _classes)
        {
            var m = vm.ToModel();
            _map[m] = vm;
            doc.Classes.Add(m);
        }
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        IssueList.ItemsSource = list.Select(i =>
        {
            ClassVm? vm = null;
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
        try { result = Services.StaticData.SaveClasses(doc); }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Could not write componentclasses.json:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }
        foreach (var vm in _classes) vm.IdFollowsName = false;   // saved IDs are frozen
        SortInPlace();
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        ShowIssues(Services.StaticData.ValidateClasses(BuildDocument()));
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
            var r = MessageBox.Show(this, "Save changes to the component classes?", "Component Classes",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ class list

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateCount();
    }

    void ClassList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Detail.DataContext = Selected;
        Detail.IsEnabled = Selected != null;
    }

    void AddClass_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        FilterBox.Text = "";
        var vm = ClassVm.CreateNew(_tracker);
        _classes.Add(vm);
        ClassList.SelectedItem = vm;
        ClassList.ScrollIntoView(vm);

        // Put the cursor in the Name box with its text selected, so typing replaces "New Class" straight away
        // (after the details panel has switched to the new class and been enabled).
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    void DeleteClass_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } sel) return;
        if (MessageBox.Show(this, $"Delete the class '{sel.Name}'?\n\nSchematics that still use it will be flagged the next time the schematics are validated.",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _classes.Remove(sel);
        _tracker.Touch();
    }

    // ------------------------------------------------------------ members

    void AddMember_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } cls) return;
        var m = new ClassMemberVm(_tracker);
        cls.Members.Add(m);
        MemberGrid.SelectedItem = m;
        MemberGrid.ScrollIntoView(m);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MemberGrid.CurrentCell = new DataGridCellInfo(m, MemberGrid.Columns[0]);
            MemberGrid.BeginEdit();
        }), DispatcherPriority.Background);
    }

    void DeleteMember_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is { } cls && MemberGrid.SelectedItem is ClassMemberVm m) cls.Members.Remove(m);
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => MoveMember(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => MoveMember(1);

    void MoveMember(int delta)
    {
        Commit();
        if (Selected is not { } cls || MemberGrid.SelectedItem is not ClassMemberVm m) return;
        int i = cls.Members.IndexOf(m), j = i + delta;
        if (j < 0 || j >= cls.Members.Count) return;
        cls.Members.Move(i, j);
        MemberGrid.SelectedItem = m;
    }

    void IssueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueRow { Vm: { } vm })
        {
            FilterBox.Text = "";
            ClassList.SelectedItem = vm;
            ClassList.ScrollIntoView(vm);
        }
    }

    void UpdateCount()
    {
        int total = _classes.Count;
        int shown = _view.Cast<object>().Count();
        CountText.Text = shown == total
            ? $"{total} {(total == 1 ? "class" : "classes")}"
            : $"{shown} of {total} classes shown";
    }
}
