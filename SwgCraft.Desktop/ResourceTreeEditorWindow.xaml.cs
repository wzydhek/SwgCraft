using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public partial class ResourceTreeEditorWindow : Window
{
    sealed record IssueRow(string Text, ResourceNodeVm? Vm);

    readonly ChangeTracker _tracker = new();
    readonly ObservableCollection<ResourceNodeVm> _roots = new();
    Dictionary<object, ResourceNodeVm> _map = new(ReferenceEqualityComparer.Instance);

    public ResourceTreeEditorWindow()
    {
        InitializeComponent();
        NodeTree.ItemsSource = _roots;
        _tracker.Changed += OnTrackerChanged;
        LoadFromDisk();
    }

    ResourceNodeVm? Selected => NodeTree.SelectedItem as ResourceNodeVm;

    ObservableCollection<ResourceNodeVm> SiblingsOf(ResourceNodeVm vm) => vm.Parent?.Children ?? _roots;

    static IEnumerable<ResourceNodeVm> Flatten(IEnumerable<ResourceNodeVm> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    void SetStatus(string text) => StatusText.Text = text;

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadResources();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Resource tree", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _roots.Clear();
        foreach (var n in load.Value.Nodes) _roots.Add(ResourceNodeVm.FromModel(n, _tracker, null));
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus(load.FileExisted
            ? $"Loaded {Flatten(_roots).Count(v => v.IsLeaf)} resource types from {Services.StaticData.ResourcesPath}"
            : $"No resources.json yet; it will be created at {Services.StaticData.ResourcesPath}");
        return true;
    }

    ResourceTreeDocument BuildDocument()
    {
        _map = new Dictionary<object, ResourceNodeVm>(ReferenceEqualityComparer.Instance);
        var doc = new ResourceTreeDocument();
        foreach (var r in _roots) doc.Nodes.Add(r.ToModel(_map));
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        IssueList.ItemsSource = list.Select(i =>
        {
            ResourceNodeVm? vm = null;
            if (i.Subject != null) _map.TryGetValue(i.Subject, out vm);
            return new IssueRow($"{(i.Severity == Severity.Error ? "ERROR" : "warn ")}  {i.Message}", vm);
        }).ToList();
        SetStatus($"{list.Count(i => i.Severity == Severity.Error)} error(s), {list.Count(i => i.Severity == Severity.Warning)} warning(s)");
    }

    bool Save()
    {
        var doc = BuildDocument();
        var result = Services.StaticData.SaveResources(doc);
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }
        foreach (var vm in Flatten(_roots)) vm.IdFollowsName = false;   // saved IDs are frozen
        Services.SetTree(doc);
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e) => ShowIssues(ResourceTreeValidator.Validate(BuildDocument()));

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker.Dirty &&
            MessageBox.Show(this, "Discard unsaved changes and reload from disk?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        LoadFromDisk();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_tracker.Dirty)
        {
            var r = MessageBox.Show(this, "Save changes to the resource tree?", "Resource Tree Editor",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ selection

    void NodeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        Detail.DataContext = e.NewValue as ResourceNodeVm;
        Detail.Visibility = e.NewValue == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void SelectNode(ResourceNodeVm vm)
    {
        for (var p = vm.Parent; p != null; p = p.Parent) p.IsExpanded = true;
        vm.IsSelected = true;
    }

    void IssueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueRow { Vm: { } vm }) SelectNode(vm);
    }

    // ------------------------------------------------------------ editing

    void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var vm = ResourceNodeVm.CreateNew(_tracker, null);
        _roots.Add(vm);
        SelectNode(vm);
        UpdateCounts();
    }

    void AddChild_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } parent) return;
        if (parent.IsLeaf && parent.HasLeafData &&
            MessageBox.Show(this, $"'{parent.Name}' is a resource type with ranges/IDs. Adding a child turns it into a group and clears those. Continue?",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (parent.IsLeaf) parent.ClearLeafData();
        var child = ResourceNodeVm.CreateNew(_tracker, parent);
        parent.Children.Add(child);
        SelectNode(child);
        UpdateCounts();
    }

    void AddSibling_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) { AddRoot_Click(sender, e); return; }
        var list = SiblingsOf(sel);
        var vm = ResourceNodeVm.CreateNew(_tracker, sel.Parent);
        list.Insert(list.IndexOf(sel) + 1, vm);
        SelectNode(vm);
        UpdateCounts();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) return;
        int below = Flatten(sel.Children).Count();
        var msg = below == 0 ? $"Delete '{sel.Name}'?" : $"Delete '{sel.Name}' and its {below} descendant node(s)?";
        if (MessageBox.Show(this, msg, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SiblingsOf(sel).Remove(sel);
        _tracker.Touch();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    void Move(int delta)
    {
        if (Selected is not { } sel) return;
        var list = SiblingsOf(sel);
        int i = list.IndexOf(sel), j = i + delta;
        if (j < 0 || j >= list.Count) return;
        list.Move(i, j);
        _tracker.Touch();
        sel.IsSelected = true;
    }

    void OnTrackerChanged()
    {
        Title = "Resource Tree Editor" + (_tracker.Dirty ? " *" : "");
        UpdateCounts();
    }

    void UpdateCounts()
    {
        var all = Flatten(_roots).ToList();
        CountText.Text = $"Nodes: {all.Count}   Resource types: {all.Count(v => v.IsLeaf)}";
    }
}
