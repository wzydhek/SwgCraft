using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public sealed class ProfessionVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _name = "";
    bool _enabled = true;

    public ProfessionVm(ChangeTracker t)
    {
        _t = t;
        Skills.CollectionChanged += SkillsChanged;
    }

    /// <summary>New professions derive their ID from the name until the first save or a manual ID edit. Saved IDs never change by themselves.</summary>
    public bool IdFollowsName { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            if (IdFollowsName && Set(ref _id, Slug.From(value), nameof(Id))) RefreshSkillIds();
        }
    }

    public string Id
    {
        get => _id;
        set
        {
            if (!Set(ref _id, value)) return;
            IdFollowsName = false;
            RefreshSkillIds();
        }
    }

    /// <summary>The "Include in builds" switch. It is a personal setting (settings.json), not part of professions.json.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public ObservableCollection<SkillVm> Skills { get; } = new();

    /// <summary>For the professions grid.</summary>
    public int SkillCount => Skills.Count;

    void SkillsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(SkillCount));
        _t.Touch();
    }

    /// <summary>Skills whose ID still follows their name pick up the new profession ID.</summary>
    void RefreshSkillIds()
    {
        foreach (var s in Skills) s.RefreshId();
    }

    protected override void OnChanged() => _t.Touch();

    public static ProfessionVm CreateNew(ChangeTracker t)
    {
        var vm = new ProfessionVm(t) { IdFollowsName = true };
        vm.Name = "New Profession";
        return vm;
    }

    public static ProfessionVm FromModel(Profession p, bool enabled, ChangeTracker t)
    {
        var vm = new ProfessionVm(t) { _id = p.Id, _name = p.Name, _enabled = enabled };
        foreach (var s in p.Skills) vm.Skills.Add(SkillVm.FromModel(s, vm, t));
        return vm;
    }

    public Profession ToModel()
    {
        var p = new Profession { Id = Id.Trim(), Name = Name.Trim() };
        foreach (var s in Skills) p.Skills.Add(s.ToModel());
        return p;
    }
}

/// <summary>
/// One skill of a profession. A new skill's ID is built from the profession's ID and the skill's name
/// ("artisan" + "Novice Artisan" -> "artisan_novice_artisan") until the first save or a manual ID edit.
/// </summary>
public sealed class SkillVm : ObservableObject
{
    readonly ChangeTracker _t;
    readonly ProfessionVm _owner;
    string _id = "", _name = "";

    public SkillVm(ProfessionVm owner, ChangeTracker t)
    {
        _owner = owner;
        _t = t;
    }

    public bool IdFollowsName { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            RefreshId();
        }
    }

    public string Id
    {
        get => _id;
        set { if (Set(ref _id, value)) IdFollowsName = false; }
    }

    /// <summary>Rebuilds the ID from the profession ID and the name, if it is still following them.</summary>
    public void RefreshId()
    {
        if (IdFollowsName) Set(ref _id, ProfessionSkill.IdFor(_owner.Id, _name), nameof(Id));
    }

    protected override void OnChanged() => _t.Touch();

    public static SkillVm CreateNew(ProfessionVm owner, ChangeTracker t)
    {
        var vm = new SkillVm(owner, t) { IdFollowsName = true };
        vm.RefreshId();   // "artisan_" until the name is typed
        return vm;
    }

    public static SkillVm FromModel(ProfessionSkill s, ProfessionVm owner, ChangeTracker t) =>
        new(owner, t) { _id = s.Id, _name = s.Name };

    public ProfessionSkill ToModel() => new() { Id = Id.Trim(), Name = Name.Trim() };
}
