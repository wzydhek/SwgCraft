using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a persisted property: raises change notification and marks the document dirty.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        OnChanged();
        return true;
    }

    /// <summary>Sets a UI-only property (selection, expansion): no dirty flag.</summary>
    protected bool SetQuiet<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected virtual void OnChanged() { }
}

public sealed class ChangeTracker
{
    public bool Dirty { get; private set; }
    public event Action? Changed;
    public void Touch() { Dirty = true; Changed?.Invoke(); }
    public void Reset() { Dirty = false; Changed?.Invoke(); }
}

public sealed class StatRangeVm : ObservableObject
{
    readonly ChangeTracker _t;
    bool _applies;
    int _min, _max;

    public StatRangeVm(Stat stat, ChangeTracker t) { Stat = stat; _t = t; }

    public Stat Stat { get; }
    public string Label => $"{Stat} - {StatInfo.FullName(Stat)}";

    public bool Applies
    {
        get => _applies;
        set
        {
            if (Set(ref _applies, value) && value && _min == 0 && _max == 0) { Min = 1; Max = 1000; }
        }
    }
    public int Min { get => _min; set => Set(ref _min, value); }
    public int Max { get => _max; set => Set(ref _max, value); }

    protected override void OnChanged() => _t.Touch();
}

public sealed class ResourceNodeVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _name = "", _ghId = "";
    bool _isExpanded, _isSelected;
    readonly SortedDictionary<string, string> _otherExternal = new(StringComparer.Ordinal);

    public ResourceNodeVm(ChangeTracker t, ResourceNodeVm? parent)
    {
        _t = t;
        Parent = parent;
        foreach (var s in StatInfo.All) Ranges.Add(new StatRangeVm(s, t));
        Children.CollectionChanged += (_, _) => { Raise(nameof(IsLeaf)); Raise(nameof(KindText)); };
    }

    public ResourceNodeVm? Parent { get; }
    public ObservableCollection<ResourceNodeVm> Children { get; } = new();
    public ObservableCollection<StatRangeVm> Ranges { get; } = new();

    /// <summary>New nodes derive their ID from the name until the first save or a manual ID edit. Saved IDs never change by themselves.</summary>
    public bool IdFollowsName { get; set; }
    /// <summary>True while the GH ID is empty or was auto-filled from the ID. Typing in the GH field turns it off.</summary>
    public bool GhIdFollowsId { get; set; } = true;

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            if (IdFollowsName)
            {
                Set(ref _id, Slug.From(value), nameof(Id));
                SyncGhIdFromId();
            }
        }
    }

    public string Id
    {
        get => _id;
        set
        {
            if (Set(ref _id, value))
            {
                IdFollowsName = false;
                SyncGhIdFromId();
            }
        }
    }

    public string GhId
    {
        get => _ghId;
        set { if (Set(ref _ghId, value)) GhIdFollowsId = string.IsNullOrWhiteSpace(value); }
    }

    void SyncGhIdFromId()
    {
        // Only leaves carry a GH ID; groups with one fail validation.
        if (GhIdFollowsId && IsLeaf) Set(ref _ghId, _id, nameof(GhId));
    }

    public bool IsLeaf => Children.Count == 0;
    public string KindText => IsLeaf
        ? "Resource type (leaf): can have stat ranges and external IDs."
        : $"Group: contains {Children.Count} child node(s). Ranges and Galaxy Harvester IDs belong on leaves.";

    public bool IsExpanded { get => _isExpanded; set => SetQuiet(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => SetQuiet(ref _isSelected, value); }

    public bool HasLeafData => (_ghId.Length > 0 && !GhIdFollowsId) || Ranges.Any(r => r.Applies); public void ClearLeafData()
    {
        GhId = "";
        foreach (var r in Ranges) r.Applies = false;
    }

    protected override void OnChanged() => _t.Touch();

    public static ResourceNodeVm CreateNew(ChangeTracker t, ResourceNodeVm? parent)
    {
        var vm = new ResourceNodeVm(t, parent) { IdFollowsName = true };
        vm.Name = "New Resource";
        return vm;
    }

    public static ResourceNodeVm FromModel(ResourceNode n, ChangeTracker t, ResourceNodeVm? parent)
    {
        var vm = new ResourceNodeVm(t, parent) { _id = n.Id, _name = n.Name };
        if (n.ExternalIds != null)
            foreach (var (source, value) in n.ExternalIds)
            {
                if (source == ExternalSources.GalaxyHarvester) { vm._ghId = value; vm.GhIdFollowsId = value.Length == 0; }
                else vm._otherExternal[source] = value;   // keep unknown sources so a round-trip loses nothing
            }
        if (n.Ranges != null)
            foreach (var r in vm.Ranges)
                if (n.Ranges.TryGetValue(r.Stat, out var range)) { r.Min = range.Min; r.Max = range.Max; r.Applies = true; }
        if (n.Children != null)
            foreach (var c in n.Children) vm.Children.Add(FromModel(c, t, vm));
        return vm;
    }

    public ResourceNode ToModel(IDictionary<object, ResourceNodeVm>? map = null)
    {
        var n = new ResourceNode { Id = Id.Trim(), Name = Name.Trim() };
        if (map != null) map[n] = this;

        var ext = new SortedDictionary<string, string>(_otherExternal, StringComparer.Ordinal);
        if (GhId.Trim().Length > 0) ext[ExternalSources.GalaxyHarvester] = GhId.Trim();
        if (ext.Count > 0) n.ExternalIds = ext;

        if (Children.Count == 0)
        {
            var ranges = new SortedDictionary<Stat, StatRange>();
            foreach (var r in Ranges.Where(r => r.Applies)) ranges[r.Stat] = new StatRange { Min = r.Min, Max = r.Max };
            if (ranges.Count > 0) n.Ranges = ranges;
        }
        else n.Children = Children.Select(c => c.ToModel(map)).ToList();
        return n;
    }
}
