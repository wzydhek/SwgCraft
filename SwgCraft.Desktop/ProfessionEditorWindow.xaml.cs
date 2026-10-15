using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public partial class ProfessionEditorWindow : Window
{
    sealed record IssueRow(string Text, ProfessionVm? Vm);

    readonly ChangeTracker _tracker = new();
    readonly ObservableCollection<ProfessionVm> _items = new();
    Dictionary<object, ProfessionVm> _map = new(ReferenceEqualityComparer.Instance);

    public ProfessionEditorWindow()
    {
        InitializeComponent();
        ProfGrid.ItemsSource = _items;
        _tracker.Changed += () => Title = "Professions" + (_tracker.Dirty ? " *" : "");
        if (!LoadFromDisk()) Loaded += (_, _) => Close();   // never edit on top of a file that failed to load
    }

    ProfessionVm? Selected => ProfGrid.SelectedItem as ProfessionVm;

    void SetStatus(string text) => StatusText.Text = text;

    void Commit()
    {
        ProfGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SkillGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadProfessions();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Professions", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _items.Clear();
        foreach (var p in load.Value.Professions)
            _items.Add(ProfessionVm.FromModel(p, Services.Settings.IsProfessionEnabled(p.Id), _tracker));
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus(load.FileExisted
            ? $"Loaded {_items.Count} profession(s) from {Services.StaticData.ProfessionsPath}"
            : $"No professions.json yet; it will be created at {Services.StaticData.ProfessionsPath}");
        return true;
    }

    ProfessionsDocument BuildDocument()
    {
        _map = new Dictionary<object, ProfessionVm>(ReferenceEqualityComparer.Instance);
        var doc = new ProfessionsDocument();
        foreach (var vm in _items)
        {
            var p = vm.ToModel();
            _map[p] = vm;
            doc.Professions.Add(p);
        }
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        IssueList.ItemsSource = list.Select(i =>
        {
            ProfessionVm? vm = null;
            if (i.Subject != null) _map.TryGetValue(i.Subject, out vm);
            return new IssueRow($"{(i.Severity == Severity.Error ? "ERROR" : "warn ")}  {i.Message}", vm);
        }).ToList();
        SetStatus($"{list.Count(i => i.Severity == Severity.Error)} error(s), {list.Count(i => i.Severity == Severity.Warning)} warning(s)");
    }

    bool Save()
    {
        Commit();
        var doc = BuildDocument();
        var result = Services.StaticData.SaveProfessions(doc);
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }

        foreach (var vm in _items)   // saved IDs are frozen
        {
            vm.IdFollowsName = false;
            foreach (var s in vm.Skills) s.IdFollowsName = false;
        }

        // The personal on/off switches go to settings.json; rebuilding the list also drops entries for deleted professions.
        Services.Settings.DisabledProfessions = _items.Where(v => !v.Enabled).Select(v => v.Id.Trim()).ToList();
        try { SettingsStore.Save(Services.Settings); }
        catch (IOException ex)
        {
            MessageBox.Show(this, "The professions were saved, but settings.json could not be written:\n" + ex.Message,
                            Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Services.SetProfessions(doc);
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        ShowIssues(ProfessionValidator.Validate(BuildDocument()));
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
            var r = MessageBox.Show(this, "Save changes to the professions?", "Professions",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ editing

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var vm = ProfessionVm.CreateNew(_tracker);
        int at = Selected == null ? _items.Count : _items.IndexOf(Selected) + 1;
        _items.Insert(at, vm);
        ProfGrid.SelectedItem = vm;
        ProfGrid.ScrollIntoView(vm);

        // Start typing the name straight away (the new row's cells exist only after layout).
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ProfGrid.CurrentCell = new DataGridCellInfo(vm, ProfGrid.Columns[0]);
            ProfGrid.BeginEdit();
        }), DispatcherPriority.Background);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } sel) return;
        var skills = sel.Skills.Count == 0 ? "" : $" and its {sel.Skills.Count} skill(s)";
        if (MessageBox.Show(this, $"Delete the profession '{sel.Name}'{skills}?", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _items.Remove(sel);
        _tracker.Touch();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    void Move(int delta)
    {
        Commit();
        if (Selected is not { } sel) return;
        int i = _items.IndexOf(sel), j = i + delta;
        if (j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        _tracker.Touch();
        ProfGrid.SelectedItem = sel;
    }

    // ------------------------------------------------------------ skills of the selected profession

    void AddSkill_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } prof)
        {
            SetStatus("Select a profession first, then add its skills.");
            return;
        }
        var vm = SkillVm.CreateNew(prof, _tracker);
        int at = SkillGrid.SelectedItem is SkillVm cur ? prof.Skills.IndexOf(cur) + 1 : prof.Skills.Count;
        prof.Skills.Insert(at, vm);
        SkillGrid.SelectedItem = vm;
        SkillGrid.ScrollIntoView(vm);

        // Start typing the name straight away; the ID fills itself in as you type.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SkillGrid.CurrentCell = new DataGridCellInfo(vm, SkillGrid.Columns[0]);
            SkillGrid.BeginEdit();
        }), DispatcherPriority.Background);
    }

    void DeleteSkill_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is { } prof && SkillGrid.SelectedItem is SkillVm s) prof.Skills.Remove(s);
    }

    void SkillUp_Click(object sender, RoutedEventArgs e) => MoveSkill(-1);
    void SkillDown_Click(object sender, RoutedEventArgs e) => MoveSkill(1);

    void MoveSkill(int delta)
    {
        Commit();
        if (Selected is not { } prof || SkillGrid.SelectedItem is not SkillVm s) return;
        int i = prof.Skills.IndexOf(s), j = i + delta;
        if (i < 0 || j < 0 || j >= prof.Skills.Count) return;
        prof.Skills.Move(i, j);
        SkillGrid.SelectedItem = s;
    }

    void IssueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueRow { Vm: { } vm })
        {
            ProfGrid.SelectedItem = vm;
            ProfGrid.ScrollIntoView(vm);
        }
    }
}
