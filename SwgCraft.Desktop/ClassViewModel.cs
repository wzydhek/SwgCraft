using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

/// <summary>One choice in a picker: Key is what is stored, Display is what the user sees.</summary>
public sealed record RefOption(string Key, string Display);

public sealed class ClassMemberVm : ObservableObject
{
    const string SchematicPrefix = "schematic:";
    const string LootPrefix = "loot:";

    readonly ChangeTracker _t;
    string _key = "";

    public ClassMemberVm(ChangeTracker t) => _t = t;

    public static string KeyFor(MemberKind kind, string id) =>
        (kind == MemberKind.Schematic ? SchematicPrefix : LootPrefix) + id;

    /// <summary>"schematic:id" or "loot:id". A null write (a picker losing its list) is ignored so a refresh can't wipe the choice.</summary>
    public string Key
    {
        get => _key;
        set { if (value != null) Set(ref _key, value); }
    }

    /// <summary>Re-reads Key in the picker after its list of options was replaced.</summary>
    public void RaiseKey() => Raise(nameof(Key));

    protected override void OnChanged() => _t.Touch();

    public static ClassMemberVm FromModel(ClassMember m, ChangeTracker t) => new(t) { _key = KeyFor(m.Kind, m.Id) };

    public ClassMember ToModel()
    {
        bool loot = _key.StartsWith(LootPrefix, StringComparison.Ordinal);
        string id = loot ? _key[LootPrefix.Length..]
                  : _key.StartsWith(SchematicPrefix, StringComparison.Ordinal) ? _key[SchematicPrefix.Length..]
                  : "";
        return new ClassMember { Kind = loot ? MemberKind.LootItem : MemberKind.Schematic, Id = id };
    }
}

public sealed class ClassVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _name = "";

    public ClassVm(ChangeTracker t)
    {
        _t = t;
        Members.CollectionChanged += (_, _) => _t.Touch();
    }

    public ObservableCollection<ClassMemberVm> Members { get; } = new();

    /// <summary>New classes derive their ID from the name until the first save or a manual ID edit. Saved IDs never change by themselves.</summary>
    public bool IdFollowsName { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            if (IdFollowsName) Set(ref _id, Slug.From(value), nameof(Id));
        }
    }

    public string Id
    {
        get => _id;
        set { if (Set(ref _id, value)) IdFollowsName = false; }
    }

    protected override void OnChanged() => _t.Touch();

    public static ClassVm CreateNew(ChangeTracker t)
    {
        var vm = new ClassVm(t) { IdFollowsName = true };
        vm.Name = "New Class";
        return vm;
    }

    public static ClassVm FromModel(ComponentClass c, ChangeTracker t)
    {
        var vm = new ClassVm(t) { _id = c.Id, _name = c.Name };
        foreach (var m in c.Members) vm.Members.Add(ClassMemberVm.FromModel(m, t));
        return vm;
    }

    public ComponentClass ToModel()
    {
        var c = new ComponentClass { Id = Id.Trim(), Name = Name.Trim() };
        foreach (var m in Members) c.Members.Add(m.ToModel());
        return c;
    }
}
