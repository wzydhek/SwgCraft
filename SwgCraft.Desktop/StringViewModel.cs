using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

/// <summary>One .stf string table in the Strings editor.</summary>
public sealed class StringFileVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _name = "";

    StringFileVm(ChangeTracker t, IEnumerable<StringEntryVm> entries)
    {
        _t = t;
        Entries = new ObservableCollection<StringEntryVm>(entries);   // filled without raising an event per string
        Entries.CollectionChanged += EntriesChanged;
    }

    public StringFileVm(ChangeTracker t) : this(t, Array.Empty<StringEntryVm>()) { }

    /// <summary>Path below the import folder without the extension, e.g. "obj_n" or "space/space_item".</summary>
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Raise(nameof(Label)); }
    }

    public ObservableCollection<StringEntryVm> Entries { get; }

    /// <summary>For the file list: "obj_n (1,234)".</summary>
    public string Label => $"{_name} ({Entries.Count:N0})";

    void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(Label));
        _t.Touch();
    }

    protected override void OnChanged() => _t.Touch();

    public static StringFileVm FromModel(StringFile m, ChangeTracker t) =>
        new(t, m.Entries.Select(e => StringEntryVm.FromModel(e, t))) { _name = m.Name };

    public StringFile ToModel()
    {
        var f = new StringFile { Name = _name.Trim() };
        foreach (var e in Entries) f.Entries.Add(e.ToModel());
        return f;
    }
}

public sealed class StringEntryVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _value = "";

    public StringEntryVm(ChangeTracker t) => _t = t;

    public string Id { get => _id; set => Set(ref _id, value ?? ""); }
    public string Value { get => _value; set => Set(ref _value, value ?? ""); }

    protected override void OnChanged() => _t.Touch();

    public static StringEntryVm FromModel(StringEntry m, ChangeTracker t) => new(t) { _id = m.Id, _value = m.Value };

    /// <summary>The ID is trimmed; the text is kept exactly as it is (it can have meaningful spaces and line breaks).</summary>
    public StringEntry ToModel() => new() { Id = _id.Trim(), Value = _value };
}
