using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

/// <summary>Static Data > Strings: the client's English string tables (strings.json), imported from a folder of .stf files.</summary>
public partial class StringEditorWindow : Window
{
    /// <summary>A line in the bottom pane: a validation issue, an import problem or a search result (which also names the string).</summary>
    sealed record IssueRow(string Text, StringFileVm? Vm, StringEntryVm? Entry = null);

    readonly ChangeTracker _tracker = new();
    ObservableCollection<StringFileVm> _files = new();
    ICollectionView _fileView = null!;
    Dictionary<object, StringFileVm> _map = new(ReferenceEqualityComparer.Instance);

    /// <summary>The last folder imported from, offered again next time (for this run of the app).</summary>
    static string? _lastImportFolder;

    public StringEditorWindow()
    {
        InitializeComponent();
        SetFiles(new ObservableCollection<StringFileVm>());
        _tracker.Changed += () => Title = "Strings" + (_tracker.Dirty ? " *" : "");
        if (!LoadFromDisk()) Loaded += (_, _) => Close();   // never edit on top of a file that failed to load
    }

    StringFileVm? Selected => FileList.SelectedItem as StringFileVm;

    void SetStatus(string text) => StatusText.Text = text;

    void Commit() => EntryGrid.CommitEdit(DataGridEditingUnit.Row, true);

    /// <summary>Swaps in a whole new list of files at once (loading or importing hundreds of files one by one would be slow).</summary>
    void SetFiles(ObservableCollection<StringFileVm> files)
    {
        if (_files != null) _files.CollectionChanged -= FilesChanged;
        _files = files;
        _files.CollectionChanged += FilesChanged;
        _fileView = CollectionViewSource.GetDefaultView(_files);
        _fileView.Filter = o =>
        {
            var f = FileFilterBox.Text?.Trim() ?? "";
            return f.Length == 0 || ((StringFileVm)o).Name.Contains(f, StringComparison.OrdinalIgnoreCase);
        };
        FileList.ItemsSource = _fileView;
        UpdateCount();
    }

    void FilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateCount();

    void UpdateCount()
    {
        int strings = _files.Sum(f => f.Entries.Count);
        CountText.Text = $"{_files.Count:N0} file(s), {strings:N0} string(s)";
    }

    // ------------------------------------------------------------ load / save

    bool LoadFromDisk()
    {
        var load = Services.StaticData.LoadStrings();
        if (load.Error != null)
        {
            MessageBox.Show(this, load.Error, "Strings", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        FileList.SelectedItem = null;
        SetFiles(new ObservableCollection<StringFileVm>(load.Value.Files.Select(f => StringFileVm.FromModel(f, _tracker))));
        _tracker.Reset();
        IssueList.ItemsSource = null;
        SetStatus(load.FileExisted
            ? $"Loaded {_files.Count:N0} string file(s) from {Services.StaticData.StringsPath}"
            : $"No strings.json yet; use Import to read the client's .stf files. It will be saved at {Services.StaticData.StringsPath}");
        return true;
    }

    StringsDocument BuildDocument()
    {
        _map = new Dictionary<object, StringFileVm>(ReferenceEqualityComparer.Instance);
        var doc = new StringsDocument();
        foreach (var vm in _files)
        {
            var m = vm.ToModel();
            _map[m] = vm;
            doc.Files.Add(m);
        }
        return doc;
    }

    void ShowIssues(IEnumerable<ValidationIssue> issues, IEnumerable<string>? extraWarnings = null)
    {
        var list = issues.OrderByDescending(i => i.Severity).ToList();
        var rows = list.Select(i =>
        {
            StringFileVm? vm = null;
            if (i.Subject != null) _map.TryGetValue(i.Subject, out vm);
            return new IssueRow($"{(i.Severity == Severity.Error ? "ERROR" : "warn ")}  {i.Message}", vm);
        }).ToList();
        if (extraWarnings != null) rows.AddRange(extraWarnings.Select(w => new IssueRow("warn   " + w, null)));
        IssueList.ItemsSource = rows;
        SetStatus($"{list.Count(i => i.Severity == Severity.Error)} error(s), {rows.Count - list.Count(i => i.Severity == Severity.Error)} warning(s)");
    }

    bool Save()
    {
        Commit();
        var doc = BuildDocument();
        SaveResult result;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            result = Services.StaticData.SaveStrings(doc);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Could not write strings.json:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { Mouse.OverrideCursor = null; }
        ShowIssues(result.Issues);
        if (!result.Saved)
        {
            SetStatus("NOT saved: fix the errors listed below first.");
            return false;
        }
        _tracker.Reset();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss}. " + StatusText.Text);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        ShowIssues(Services.StaticData.ValidateStrings(BuildDocument()));
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
            var r = MessageBox.Show(this, "Save changes to the strings?", "Strings",
                                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ import

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the client's string\\en folder (all .stf files below it are read)",
        };
        if (_lastImportFolder != null && Directory.Exists(_lastImportFolder)) dlg.InitialDirectory = _lastImportFolder;
        if (dlg.ShowDialog(this) != true) return;
        var root = dlg.FolderName;
        _lastImportFolder = root;

        StfImportResult result;
        MainToolBar.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var progress = new Progress<string>(SetStatus);
            SetStatus($"Reading .stf files below {root}...");
            result = await Task.Run(() => StfImporter.ImportFolder(root, progress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not read {root}:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Import cancelled.");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            MainToolBar.IsEnabled = true;
        }

        if (result.Document.Files.Count == 0)
        {
            ShowIssues(Array.Empty<ValidationIssue>(), result.Problems);
            SetStatus($"No readable .stf files were found below {root}. Nothing was changed.");
            return;
        }

        if (_files.Count > 0 || _tracker.Dirty)
        {
            var answer = MessageBox.Show(this,
                $"Replace the {_files.Count:N0} stored string file(s) with the {result.Document.Files.Count:N0} file(s) " +
                $"({result.StringCount:N0} strings) read from\n{root}?\n\nFiles that are not in that folder will be removed. Nothing is saved until you press Save.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { SetStatus("Import cancelled; nothing was changed."); return; }
        }

        FileList.SelectedItem = null;
        SetFiles(new ObservableCollection<StringFileVm>(result.Document.Files.Select(f => StringFileVm.FromModel(f, _tracker))));
        _tracker.Touch();
        ShowIssues(Array.Empty<ValidationIssue>(), result.Problems);
        SetStatus($"Imported {result.Document.Files.Count:N0} file(s), {result.StringCount:N0} string(s) from {root}" +
                  (result.Problems.Count > 0 ? $"; {result.Problems.Count} problem(s) listed below" : "") + ". Remember to Save.");
    }

    // ------------------------------------------------------------ files

    void FileFilterBox_TextChanged(object sender, TextChangedEventArgs e) => _fileView?.Refresh();

    void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Commit();
        Detail.DataContext = Selected;
        Detail.IsEnabled = Selected != null;
        EntryGrid.ItemsSource = Selected == null ? null : EntryView(Selected);
    }

    /// <summary>The selected file's strings, filtered by the Find box.</summary>
    ICollectionView EntryView(StringFileVm file)
    {
        var view = CollectionViewSource.GetDefaultView(file.Entries);
        view.Filter = o =>
        {
            var f = EntryFilterBox.Text?.Trim() ?? "";
            var en = (StringEntryVm)o;
            return f.Length == 0 || en.Id.Contains(f, StringComparison.OrdinalIgnoreCase)
                                 || en.Value.Contains(f, StringComparison.OrdinalIgnoreCase);
        };
        return view;
    }

    void EntryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EntryGrid.ItemsSource is ICollectionView v) v.Refresh();
    }

    void AddFile_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        FileFilterBox.Text = "";
        var vm = new StringFileVm(_tracker) { Name = "new_file" };
        _files.Add(vm);
        FileList.SelectedItem = vm;
        FileList.ScrollIntoView(vm);
        Dispatcher.BeginInvoke(new Action(() => { FileNameBox.Focus(); FileNameBox.SelectAll(); }), DispatcherPriority.Input);
    }

    void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } sel) return;
        if (MessageBox.Show(this, $"Delete the string file '{sel.Name}' and its {sel.Entries.Count:N0} string(s)?", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _files.Remove(sel);
        _tracker.Touch();
    }

    // ------------------------------------------------------------ strings

    void AddString_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is not { } file) return;
        EntryFilterBox.Text = "";
        var vm = new StringEntryVm(_tracker);
        int at = EntryGrid.SelectedItem is StringEntryVm cur ? file.Entries.IndexOf(cur) + 1 : file.Entries.Count;
        file.Entries.Insert(at, vm);
        EntryGrid.SelectedItem = vm;
        EntryGrid.ScrollIntoView(vm);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            EntryGrid.CurrentCell = new DataGridCellInfo(vm, EntryGrid.Columns[0]);
            EntryGrid.BeginEdit();
        }), DispatcherPriority.Background);
        UpdateCount();
    }

    void DeleteString_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (Selected is { } file && EntryGrid.SelectedItem is StringEntryVm s)
        {
            file.Entries.Remove(s);
            UpdateCount();
        }
    }

    void IssueList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is not IssueRow { Vm: { } vm } row) return;
        FileFilterBox.Text = "";
        if (row.Entry != null) EntryFilterBox.Text = "";   // the Find box could hide the string we're jumping to
        FileList.SelectedItem = vm;
        FileList.ScrollIntoView(vm);
        if (row.Entry is { } entry)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                EntryGrid.SelectedItem = entry;
                EntryGrid.ScrollIntoView(entry);
            }), DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------ search by exact text

    void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        RunSearch();
    }

    /// <summary>
    /// Lists every string (in all files, including unsaved edits) whose whole text equals the search text,
    /// as "@file:id" in the bottom pane. Spaces at either end are ignored on both sides; case only matters with Match case.
    /// </summary>
    void RunSearch()
    {
        Commit();
        var text = SearchBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            SetStatus("Type the text to search for.");
            return;
        }
        var comparison = MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var rows = new List<IssueRow>();
        foreach (var file in _files)
            foreach (var entry in file.Entries)
                if (string.Equals(entry.Value.Trim(), text, comparison))
                    rows.Add(new IssueRow($"@{file.Name}:{entry.Id}", file, entry));

        IssueList.ItemsSource = rows;
        SetStatus(rows.Count == 0
            ? $"No string's text is exactly \"{text}\"."
            : $"{rows.Count:N0} string(s) whose text is exactly \"{text}\" (listed below). Double-click one to go to it.");
    }
}
